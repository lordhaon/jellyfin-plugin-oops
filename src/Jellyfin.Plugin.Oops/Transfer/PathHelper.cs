using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Oops.Transfer;

/// <summary>
/// Path comparison and media-file classification helpers.
/// </summary>
internal static class PathHelper
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".webm",
        ".flv", ".iso", ".vob", ".ogv", ".3gp", ".divx", ".xvid", ".rmvb", ".strm", ".asf", ".f4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".ape",
        ".wv", ".dsf", ".dff", ".aiff", ".aif", ".mka", ".mpc"
    };

    private static readonly HashSet<string> BookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".cbz", ".cbr", ".cb7", ".cbt", ".fb2", ".djvu"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tbn"
    };

    // Folder names Jellyfin treats as extras / disc structures inside a movie folder.
    private static readonly HashSet<string> OwnedSubfolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "trailers", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes",
        "shorts", "other", "clips", "samples", "sample", "backdrops", "theme-music", "theme music", "themes",
        "specials", "video_ts", "audio_ts", "bdmv", "certificate", "metadata", "subs", "subtitles", "artwork"
    };

    private static readonly Regex PartSuffix = new(
        @"[\s_.\-]*(cd|dvd|part|pt|disc|disk)[\s_.\-]*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Gets the comparison used for paths on this OS.
    /// </summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length > root.Length)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full;
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>
    /// True when <paramref name="path"/> is strictly inside <paramref name="parent"/>.
    /// </summary>
    public static bool IsStrictlyUnder(string path, string parent)
    {
        var p = Normalize(path);
        var root = Normalize(parent);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return p.StartsWith(root, Comparison);
    }

    public static bool IsUnderOrSame(string path, string parent) => SamePath(path, parent) || IsStrictlyUnder(path, parent);

    public static bool IsPrimaryMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext) || BookExtensions.Contains(ext);
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static string StripPartSuffix(string stem) => PartSuffix.Replace(stem, string.Empty).Trim();

    public static bool IsOwnedSubfolderName(string name) => OwnedSubfolderNames.Contains(name);

    public static bool ContainsPrimaryMedia(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(IsPrimaryMedia);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// First segment of a relative path, e.g. "Show Name" for "Show Name/Season 1/ep.mkv".
    /// </summary>
    public static string FirstSegment(string relativePath)
    {
        var parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : relativePath;
    }
}
