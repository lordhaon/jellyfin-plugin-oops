using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Oops.Transfer;

/// <summary>
/// A Jellyfin library ("virtual folder").
/// </summary>
internal sealed class LibraryInfo
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public CollectionTypeOptions? CollectionType { get; init; }

    public required string[] Locations { get; init; }
}

/// <summary>
/// One thing on disk that moves as a whole: a folder, or a single file plus its sidecars.
/// </summary>
internal sealed class MoveUnit
{
    public required BaseItem Item { get; init; }

    public required LibraryInfo SourceLibrary { get; init; }

    /// <summary>Gets the library folder (location) the unit currently lives in.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Gets the folder or main file being moved.</summary>
    public required string SourcePath { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Gets sidecar files/folders that travel with a single-file unit (subtitles, nfo, artwork, trickplay).</summary>
    public List<string> Sidecars { get; } = new();

    /// <summary>Gets the path of <see cref="SourcePath"/> relative to <see cref="SourceRoot"/>.</summary>
    public string RelativePath => Path.GetRelativePath(SourceRoot, SourcePath);
}

/// <summary>
/// Works out what has to move on disk for a given Jellyfin item.
/// </summary>
internal sealed class MovePlanner
{
    private readonly ILibraryManager _libraryManager;

    public MovePlanner(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<LibraryInfo> GetLibraries()
    {
        var result = new List<LibraryInfo>();
        foreach (var vf in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(vf.ItemId, out var id) || vf.Locations is null || vf.Locations.Length == 0)
            {
                continue;
            }

            result.Add(new LibraryInfo
            {
                Id = id,
                Name = vf.Name,
                CollectionType = vf.CollectionType,
                Locations = vf.Locations
            });
        }

        return result;
    }

    /// <summary>
    /// Finds the library + library folder that contains <paramref name="path"/> (deepest match wins).
    /// </summary>
    public (LibraryInfo Library, string Root)? FindLibrary(string path, IReadOnlyList<LibraryInfo> libraries)
    {
        (LibraryInfo Library, string Root)? best = null;
        foreach (var lib in libraries)
        {
            foreach (var loc in lib.Locations)
            {
                if (PathHelper.IsUnderOrSame(path, loc)
                    && (best is null || PathHelper.Normalize(loc).Length > PathHelper.Normalize(best.Value.Root).Length))
                {
                    best = (lib, loc);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Plans the move for one item. Returns null and sets <paramref name="error"/> when it can't be moved.
    /// </summary>
    public MoveUnit? Plan(BaseItem item, IReadOnlyList<LibraryInfo> libraries, out string? error)
    {
        error = null;

        if (item is CollectionFolder || item is UserRootFolder || item is AggregateFolder || item is UserView)
        {
            error = "Whole libraries can't be moved.";
            return null;
        }

        if (item is BoxSet || item is Playlist)
        {
            error = "Collections and playlists aren't files on disk, so there's nothing to move.";
            return null;
        }

        if (item.ExtraType.HasValue)
        {
            error = "Extras move together with the movie or show they belong to.";
            return null;
        }

        if (item.IsVirtualItem || string.IsNullOrEmpty(item.Path))
        {
            error = "This item doesn't exist on disk (it's a placeholder or virtual item).";
            return null;
        }

        var path = item.Path;
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path))
        {
            error = "The file couldn't be found on disk: " + path;
            return null;
        }

        var found = FindLibrary(path, libraries);
        if (found is null)
        {
            error = "This item isn't stored inside a library folder.";
            return null;
        }

        var (library, root) = found.Value;
        if (PathHelper.SamePath(path, root))
        {
            error = "This is a library's root folder and can't be moved.";
            return null;
        }

        // Folder items (series, seasons, albums, artists, photo albums) and disc-folder movies (DVD/Blu-ray).
        if (isDir)
        {
            return new MoveUnit { Item = item, SourceLibrary = library, SourceRoot = root, SourcePath = path, IsDirectory = true };
        }

        var parent = Path.GetDirectoryName(path)!;

        // Movies, books etc. that sit in their own folder move with the whole folder (artwork, extras, subtitles).
        var canOwnFolder = (item is Video && item is not Episode) || item is Book || item is AudioBook;
        if (canOwnFolder && !PathHelper.SamePath(parent, root) && OwnsFolder(path, parent))
        {
            return new MoveUnit { Item = item, SourceLibrary = library, SourceRoot = root, SourcePath = parent, IsDirectory = true };
        }

        var unit = new MoveUnit { Item = item, SourceLibrary = library, SourceRoot = root, SourcePath = path, IsDirectory = false };
        unit.Sidecars.AddRange(FindSidecars(path, allowDashImages: item is not Photo));
        return unit;
    }

    /// <summary>
    /// True when the folder holds only this title (plus its versions, parts, extras and artwork).
    /// </summary>
    private static bool OwnsFolder(string mediaFile, string folder)
    {
        var folderName = Path.GetFileName(folder);
        var stem = PathHelper.StripPartSuffix(Path.GetFileNameWithoutExtension(mediaFile));

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(sub);
                if (PathHelper.IsOwnedSubfolderName(name)
                    || name.StartsWith(Path.GetFileNameWithoutExtension(mediaFile) + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (PathHelper.ContainsPrimaryMedia(sub))
                {
                    return false;
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!PathHelper.IsPrimaryMedia(file) || PathHelper.SamePath(file, mediaFile))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                var otherStem = PathHelper.StripPartSuffix(Path.GetFileNameWithoutExtension(file));
                var isVersionOrPart = name.StartsWith(folderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(otherStem, stem, StringComparison.OrdinalIgnoreCase)
                    || otherStem.StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase);
                if (!isVersionOrPart)
                {
                    return false;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Files/folders next to <paramref name="mediaFile"/> that belong to it: "name.en.srt", "name.nfo",
    /// "name-poster.jpg", "name.trickplay/" and so on.
    /// </summary>
    private static IEnumerable<string> FindSidecars(string mediaFile, bool allowDashImages)
    {
        var dir = Path.GetDirectoryName(mediaFile)!;
        var stem = Path.GetFileNameWithoutExtension(mediaFile);
        var result = new List<string>();

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (PathHelper.SamePath(entry, mediaFile))
                {
                    continue;
                }

                var name = Path.GetFileName(entry);
                if (!name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || name.Length == stem.Length)
                {
                    continue;
                }

                var next = name[stem.Length];
                var isDir = Directory.Exists(entry);

                if (!isDir && PathHelper.IsPrimaryMedia(entry))
                {
                    continue; // another episode/track/version – never take it along
                }

                if (next == '.')
                {
                    result.Add(entry);
                }
                else if (allowDashImages && (next == '-' || next == '_') && !isDir && PathHelper.IsImage(entry))
                {
                    result.Add(entry);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    /// Drops units that are already inside another selected folder unit (e.g. a season when its series is selected).
    /// </summary>
    public static List<MoveUnit> RemoveNested(List<MoveUnit> units, Action<MoveUnit, MoveUnit> onNested)
    {
        var kept = new List<MoveUnit>();
        foreach (var unit in units)
        {
            var container = units.FirstOrDefault(other => !ReferenceEquals(other, unit)
                && other.IsDirectory
                && (PathHelper.IsStrictlyUnder(unit.SourcePath, other.SourcePath)
                    || (PathHelper.SamePath(unit.SourcePath, other.SourcePath) && units.IndexOf(other) < units.IndexOf(unit))));
            if (container is not null)
            {
                onNested(unit, container);
                continue;
            }

            kept.Add(unit);
        }

        return kept;
    }
}
