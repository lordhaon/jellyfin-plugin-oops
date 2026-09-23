using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Oops.Api;

/// <summary>
/// Body for POST /OOPS/Transfer.
/// </summary>
public sealed class TransferRequest
{
    [JsonPropertyName("itemIds")]
    public List<Guid> ItemIds { get; set; } = new();

    [JsonPropertyName("targetLibraryId")]
    public Guid TargetLibraryId { get; set; }
}

/// <summary>
/// Response for POST /OOPS/Transfer.
/// </summary>
public sealed class TransferStartedResponse
{
    [JsonPropertyName("jobId")]
    public Guid JobId { get; init; }
}

/// <summary>
/// Response for GET /OOPS/Targets.
/// </summary>
public sealed class TargetsResponse
{
    [JsonPropertyName("items")]
    public List<TargetItemInfo> Items { get; init; } = new();

    [JsonPropertyName("libraries")]
    public List<TargetLibraryInfo> Libraries { get; init; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// One selected item and whether it can be moved.
/// </summary>
public sealed class TargetItemInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("sourceLibrary")]
    public string? SourceLibrary { get; init; }

    [JsonPropertyName("canMove")]
    public bool CanMove { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// A library the selection can be moved to.
/// </summary>
public sealed class TargetLibraryInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("collectionType")]
    public string? CollectionType { get; init; }

    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;
}
