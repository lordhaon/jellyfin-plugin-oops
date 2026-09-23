using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Oops.Transfer;

/// <summary>
/// A running or finished transfer, kept in memory so the web UI can show progress.
/// </summary>
internal sealed class TransferJob
{
    private readonly object _lock = new();
    private readonly List<TransferItemResult> _items = new();

    public Guid Id { get; } = Guid.NewGuid();

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public required string TargetLibraryName { get; init; }

    public string State { get; private set; } = TransferStates.Queued;

    public string? Error { get; private set; }

    public int Total { get; private set; }

    public int Done { get; private set; }

    public DateTime? FinishedUtc { get; private set; }

    public void SetState(string state)
    {
        lock (_lock)
        {
            State = state;
        }
    }

    public void SetTotal(int total)
    {
        lock (_lock)
        {
            Total = total;
        }
    }

    public void AddResult(TransferItemResult result, bool countsAsDone = true)
    {
        lock (_lock)
        {
            _items.Add(result);
            if (countsAsDone)
            {
                Done++;
            }
        }
    }

    public void Finish(string state, string? error = null)
    {
        lock (_lock)
        {
            State = state;
            Error = error;
            FinishedUtc = DateTime.UtcNow;
        }
    }

    public bool AnyFailed()
    {
        lock (_lock)
        {
            return _items.Any(i => i.Status == TransferItemStatus.Failed);
        }
    }

    public TransferJobDto ToDto()
    {
        lock (_lock)
        {
            return new TransferJobDto
            {
                Id = Id,
                TargetLibraryName = TargetLibraryName,
                State = State,
                Error = Error,
                Total = Total,
                Done = Done,
                StartedUtc = StartedUtc,
                FinishedUtc = FinishedUtc,
                IsFinished = FinishedUtc.HasValue,
                Items = _items.ToList()
            };
        }
    }
}

internal static class TransferStates
{
    public const string Queued = "Queued";
    public const string Moving = "Moving files";
    public const string Scanning = "Scanning libraries";
    public const string Restoring = "Restoring watch state";
    public const string Completed = "Completed";
    public const string CompletedWithErrors = "Completed with errors";
    public const string Failed = "Failed";
}

internal static class TransferItemStatus
{
    public const string Moved = "Moved";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

/// <summary>
/// Outcome for one selected item.
/// </summary>
public sealed class TransferItemResult
{
    [JsonPropertyName("itemId")]
    public Guid ItemId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }
}

/// <summary>
/// Job status returned by the API.
/// </summary>
public sealed class TransferJobDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("targetLibraryName")]
    public string TargetLibraryName { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("done")]
    public int Done { get; init; }

    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; init; }

    [JsonPropertyName("finishedUtc")]
    public DateTime? FinishedUtc { get; init; }

    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; init; }

    [JsonPropertyName("items")]
    public List<TransferItemResult> Items { get; init; } = new();
}
