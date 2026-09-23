using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Oops.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oops.Transfer;

/// <summary>
/// Moves items between libraries: plans, moves files, rescans, restores watch state.
/// </summary>
public sealed class TransferService
{
    private const int MaxJobsKept = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TransferService> _logger;
    private readonly MovePlanner _planner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TransferJob> _jobs = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TransferService"/> class.
    /// </summary>
    public TransferService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryMonitor libraryMonitor,
        IFileSystem fileSystem,
        ILogger<TransferService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryMonitor = libraryMonitor;
        _fileSystem = fileSystem;
        _logger = logger;
        _planner = new MovePlanner(libraryManager);
    }

    /// <summary>
    /// Works out which libraries the given items can be moved to.
    /// </summary>
    public TargetsResponse GetTargets(IReadOnlyList<Guid> itemIds)
    {
        var libraries = _planner.GetLibraries();
        var items = new List<TargetItemInfo>();
        var units = new List<MoveUnit>();

        foreach (var id in itemIds.Distinct())
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                items.Add(new TargetItemInfo { Id = id, Name = id.ToString("N"), CanMove = false, Reason = "Item not found." });
                continue;
            }

            var unit = _planner.Plan(item, libraries, out var error);
            if (unit is null)
            {
                items.Add(new TargetItemInfo { Id = id, Name = item.Name, CanMove = false, Reason = error });
                continue;
            }

            units.Add(unit);
            items.Add(new TargetItemInfo { Id = id, Name = item.Name, CanMove = true, SourceLibrary = unit.SourceLibrary.Name });
        }

        if (units.Count == 0)
        {
            return new TargetsResponse { Items = items, Message = "Nothing selected can be transferred." };
        }

        var types = units.Select(u => u.SourceLibrary.CollectionType).Distinct().ToList();
        if (types.Count > 1)
        {
            return new TargetsResponse { Items = items, Message = "The selection comes from different kinds of libraries (e.g. movies and TV). Transfer them separately." };
        }

        var sourceIds = units.Select(u => u.SourceLibrary.Id).Distinct().ToList();
        var targets = libraries
            .Where(l => l.CollectionType == types[0])
            .Where(l => !(sourceIds.Count == 1 && sourceIds[0] == l.Id))
            .Select(l => new TargetLibraryInfo
            {
                Id = l.Id,
                Name = l.Name,
                CollectionType = l.CollectionType?.ToString(),
                Folder = l.Locations[0]
            })
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new TargetsResponse
        {
            Items = items,
            Libraries = targets,
            Message = targets.Count == 0 ? "There's no other library of the same type to move to." : null
        };
    }

    /// <summary>
    /// Queues a transfer and returns immediately; poll <see cref="GetJob"/> for progress.
    /// </summary>
    public Guid StartTransfer(IReadOnlyList<Guid> itemIds, Guid targetLibraryId)
    {
        var target = _planner.GetLibraries().FirstOrDefault(l => l.Id == targetLibraryId)
            ?? throw new ArgumentException("Target library not found.", nameof(targetLibraryId));

        var job = new TransferJob { TargetLibraryName = target.Name };
        job.SetTotal(itemIds.Distinct().Count());
        _jobs[job.Id] = job;
        TrimJobs();

        var ids = itemIds.Distinct().ToList();
        _ = Task.Run(() => RunAsync(job, ids, targetLibraryId));
        return job.Id;
    }

    /// <summary>
    /// Gets a job's current status.
    /// </summary>
    public TransferJobDto? GetJob(Guid jobId) => _jobs.TryGetValue(jobId, out var job) ? job.ToDto() : null;

    /// <summary>
    /// Gets recent jobs, newest first.
    /// </summary>
    public IReadOnlyList<TransferJobDto> GetJobs() => _jobs.Values.OrderByDescending(j => j.StartedUtc).Select(j => j.ToDto()).ToList();

    private void TrimJobs()
    {
        foreach (var old in _jobs.Values.OrderByDescending(j => j.StartedUtc).Skip(MaxJobsKept).ToList())
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }

    private async Task RunAsync(TransferJob job, List<Guid> itemIds, Guid targetLibraryId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            job.SetState(TransferStates.Moving);
            var config = Plugin.Instance?.Configuration;
            var restoreWatchState = config?.RestoreWatchState ?? true;
            var removeEmpty = config?.RemoveEmptySourceFolders ?? true;

            var libraries = _planner.GetLibraries();
            var target = libraries.FirstOrDefault(l => l.Id == targetLibraryId);
            if (target is null)
            {
                job.Finish(TransferStates.Failed, "Target library no longer exists.");
                return;
            }

            // 1. Plan everything first so nested selections (season + its series) are only moved once.
            var planned = new List<MoveUnit>();
            foreach (var id in itemIds)
            {
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    job.AddResult(new TransferItemResult { ItemId = id, Name = id.ToString("N"), Status = TransferItemStatus.Failed, Message = "Item not found." });
                    continue;
                }

                var unit = _planner.Plan(item, libraries, out var error);
                if (unit is null)
                {
                    job.AddResult(new TransferItemResult { ItemId = id, Name = item.Name, Status = TransferItemStatus.Failed, Message = error });
                    continue;
                }

                if (unit.SourceLibrary.CollectionType != target.CollectionType)
                {
                    job.AddResult(new TransferItemResult
                    {
                        ItemId = id,
                        Name = item.Name,
                        Status = TransferItemStatus.Failed,
                        Message = $"'{target.Name}' is a different kind of library than '{unit.SourceLibrary.Name}'."
                    });
                    continue;
                }

                if (unit.SourceLibrary.Id == target.Id)
                {
                    job.AddResult(new TransferItemResult { ItemId = id, Name = item.Name, Status = TransferItemStatus.Skipped, Message = "Already in this library." });
                    continue;
                }

                planned.Add(unit);
            }

            var units = MovePlanner.RemoveNested(planned, (nested, container) => job.AddResult(new TransferItemResult
            {
                ItemId = nested.Item.Id,
                Name = nested.Item.Name,
                Status = TransferItemStatus.Skipped,
                Message = $"Moved as part of '{container.Item.Name}'."
            }));

            // 2. Move files.
            var affectedLibraries = new HashSet<Guid>();
            var snapshots = new List<UserDataSnapshot>();
            foreach (var unit in units)
            {
                var targetRoot = ChooseTargetRoot(target, unit.RelativePath);
                var destPath = Path.Combine(targetRoot, unit.RelativePath);
                try
                {
                    var unitSnapshots = restoreWatchState ? SnapshotUserData(unit, destPath) : new List<UserDataSnapshot>();
                    MoveUnitOnDisk(unit, targetRoot, removeEmpty);
                    snapshots.AddRange(unitSnapshots);
                    affectedLibraries.Add(unit.SourceLibrary.Id);
                    affectedLibraries.Add(target.Id);
                    job.AddResult(new TransferItemResult
                    {
                        ItemId = unit.Item.Id,
                        Name = unit.Item.Name,
                        Status = TransferItemStatus.Moved,
                        From = unit.SourcePath,
                        To = destPath
                    });
                    _logger.LogInformation("[OOPS] Moved {Name} from {From} to {To}", unit.Item.Name, unit.SourcePath, destPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[OOPS] Failed to move {Name} ({Path})", unit.Item.Name, unit.SourcePath);
                    job.AddResult(new TransferItemResult
                    {
                        ItemId = unit.Item.Id,
                        Name = unit.Item.Name,
                        Status = TransferItemStatus.Failed,
                        Message = ex.Message,
                        From = unit.SourcePath
                    });
                }
            }

            // 3. Rescan the libraries that changed so the old entries disappear and the new ones appear.
            if (affectedLibraries.Count > 0)
            {
                job.SetState(TransferStates.Scanning);
                await RescanAsync(affectedLibraries).ConfigureAwait(false);
            }

            // 4. Put watched / favorite / resume state back on the new items.
            if (snapshots.Count > 0)
            {
                job.SetState(TransferStates.Restoring);
                RestoreUserData(snapshots);
            }

            job.Finish(job.AnyFailed() ? TransferStates.CompletedWithErrors : TransferStates.Completed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OOPS] Transfer job {JobId} failed", job.Id);
            job.Finish(TransferStates.Failed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// For libraries with several folders: prefer the one that already has e.g. the series folder, else the first.
    /// </summary>
    private static string ChooseTargetRoot(LibraryInfo target, string relativePath)
    {
        var first = PathHelper.FirstSegment(relativePath);
        foreach (var loc in target.Locations)
        {
            var candidate = Path.Combine(loc, first);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                return loc;
            }
        }

        return target.Locations[0];
    }

    private void MoveUnitOnDisk(MoveUnit unit, string targetRoot, bool removeEmpty)
    {
        if (!Directory.Exists(targetRoot))
        {
            throw new IOException($"The target library folder '{targetRoot}' doesn't exist or isn't reachable.");
        }

        // Build the file list: (source, destination).
        var moves = new List<(string Src, string Dst)>();
        var sourceDirs = new List<string>();

        void AddPath(string src)
        {
            var rel = Path.GetRelativePath(unit.SourceRoot, src);
            var dst = Path.Combine(targetRoot, rel);
            if (Directory.Exists(src))
            {
                sourceDirs.Add(src);
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    moves.Add((file, Path.Combine(targetRoot, Path.GetRelativePath(unit.SourceRoot, file))));
                }
            }
            else
            {
                moves.Add((src, dst));
            }
        }

        AddPath(unit.SourcePath);
        foreach (var sidecar in unit.Sidecars)
        {
            AddPath(sidecar);
        }

        // Pre-flight: never overwrite anything in the target library.
        var clashIndex = moves.FindIndex(m => File.Exists(m.Dst));
        if (clashIndex >= 0)
        {
            throw new IOException($"'{moves[clashIndex].Dst}' already exists in the target library. Nothing was moved.");
        }

        CheckWritable(targetRoot);

        var destPath = Path.Combine(targetRoot, unit.RelativePath);
        _libraryMonitor.ReportFileSystemChangeBeginning(unit.SourcePath);
        _libraryMonitor.ReportFileSystemChangeBeginning(destPath);

        var done = new List<(string Src, string Dst)>();
        try
        {
            foreach (var (src, dst) in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                // File.Move falls back to copy + delete when source and target are on different drives.
                File.Move(src, dst, overwrite: false);
                done.Add((src, dst));
            }
        }
        catch
        {
            // Put back whatever already moved so nothing is left half-transferred.
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(done[i].Src)!);
                    File.Move(done[i].Dst, done[i].Src, overwrite: false);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "[OOPS] Rollback failed for {Path}", done[i].Dst);
                }
            }

            RemoveEmptyDirectories(destPath, stopAt: targetRoot);
            throw;
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(unit.SourcePath, false);
            _libraryMonitor.ReportFileSystemChangeComplete(destPath, false);
        }

        if (removeEmpty)
        {
            foreach (var dir in sourceDirs)
            {
                RemoveEmptyDirectories(dir, stopAt: unit.SourceRoot);
            }
        }
    }

    private static void CheckWritable(string folder)
    {
        var probe = Path.Combine(folder, ".oops-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new IOException($"Jellyfin can't write to '{folder}'. If you use Docker, make sure the library is mounted read-write.", ex);
        }
    }

    /// <summary>
    /// Deletes <paramref name="dir"/> and its sub-folders if they contain no files. Never touches <paramref name="stopAt"/>.
    /// </summary>
    private void RemoveEmptyDirectories(string dir, string stopAt)
    {
        try
        {
            if (!Directory.Exists(dir) || PathHelper.SamePath(dir, stopAt) || !PathHelper.IsStrictlyUnder(dir, stopAt))
            {
                return;
            }

            foreach (var sub in Directory.GetDirectories(dir))
            {
                RemoveEmptyDirectories(sub, stopAt);
            }

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[OOPS] Couldn't remove empty folder {Path}", dir);
        }
    }

    private async Task RescanAsync(IEnumerable<Guid> libraryIds)
    {
        foreach (var libraryId in libraryIds)
        {
            try
            {
                if (_libraryManager.GetItemById(libraryId) is Folder folder)
                {
                    var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
                    await folder.ValidateChildren(new Progress<double>(), options, true, false, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OOPS] Rescan of library {LibraryId} failed", libraryId);
            }
        }
    }

    private List<UserDataSnapshot> SnapshotUserData(MoveUnit unit, string destPath)
    {
        var result = new List<UserDataSnapshot>();
        var items = new List<BaseItem> { unit.Item };
        if (unit.Item is Folder folder)
        {
            items.AddRange(folder.GetRecursiveChildren());
        }

        var users = _userManager.GetUsers().ToList();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path) || item.IsVirtualItem || !PathHelper.IsUnderOrSame(item.Path, unit.SourcePath))
            {
                continue;
            }

            var rel = Path.GetRelativePath(unit.SourcePath, item.Path);
            var newPath = rel == "." ? destPath : Path.Combine(destPath, rel);

            foreach (var user in users)
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is null)
                {
                    continue;
                }

                var meaningful = data.Played || data.PlayCount > 0 || data.IsFavorite || data.PlaybackPositionTicks > 0 || data.Rating.HasValue;
                if (!meaningful)
                {
                    continue;
                }

                result.Add(new UserDataSnapshot
                {
                    UserId = user.Id,
                    NewPath = newPath,
                    IsFolder = item.IsFolder,
                    Played = data.Played,
                    PlayCount = data.PlayCount,
                    IsFavorite = data.IsFavorite,
                    PlaybackPositionTicks = data.PlaybackPositionTicks,
                    LastPlayedDate = data.LastPlayedDate,
                    Rating = data.Rating,
                    AudioStreamIndex = data.AudioStreamIndex,
                    SubtitleStreamIndex = data.SubtitleStreamIndex
                });
            }
        }

        return result;
    }

    private void RestoreUserData(List<UserDataSnapshot> snapshots)
    {
        var restored = 0;
        var missing = 0;
        foreach (var group in snapshots.GroupBy(s => s.NewPath))
        {
            var first = group.First();
            var item = _libraryManager.FindByPath(first.NewPath, first.IsFolder);
            if (item is null)
            {
                missing++;
                continue;
            }

            foreach (var snap in group)
            {
                var user = _userManager.GetUserById(snap.UserId);
                if (user is null)
                {
                    continue;
                }

                try
                {
                    var keys = item.GetUserDataKeys();
                    var data = _userDataManager.GetUserData(user, item) ?? new UserItemData { Key = keys.Count > 0 ? keys[0] : item.Id.ToString("N") };

                    data.IsFavorite = snap.IsFavorite;
                    data.Rating = snap.Rating;
                    if (!snap.IsFolder)
                    {
                        data.Played = snap.Played;
                        data.PlayCount = snap.PlayCount;
                        data.PlaybackPositionTicks = snap.PlaybackPositionTicks;
                        data.LastPlayedDate = snap.LastPlayedDate;
                        data.AudioStreamIndex = snap.AudioStreamIndex;
                        data.SubtitleStreamIndex = snap.SubtitleStreamIndex;
                    }

                    _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
                    restored++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[OOPS] Couldn't restore watch state for {Path}", snap.NewPath);
                }
            }
        }

        _logger.LogInformation("[OOPS] Restored {Restored} watch-state entries ({Missing} items not found after scan)", restored, missing);
    }

    private sealed class UserDataSnapshot
    {
        public Guid UserId { get; init; }

        public required string NewPath { get; init; }

        public bool IsFolder { get; init; }

        public bool Played { get; init; }

        public int PlayCount { get; init; }

        public bool IsFavorite { get; init; }

        public long PlaybackPositionTicks { get; init; }

        public DateTime? LastPlayedDate { get; init; }

        public double? Rating { get; init; }

        public int? AudioStreamIndex { get; init; }

        public int? SubtitleStreamIndex { get; init; }
    }
}
