using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Oops.Configuration;

/// <summary>
/// OOPS settings, editable from Dashboard → Plugins → OOPS.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether watched/favorite/resume state is copied to the moved items.
    /// </summary>
    public bool RestoreWatchState { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether folders left empty after a move are deleted.
    /// </summary>
    public bool RemoveEmptySourceFolders { get; set; } = true;
}
