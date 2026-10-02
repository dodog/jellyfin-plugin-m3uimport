using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.M3UImport.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>One mapping per line: "m3u prefix =&gt; Jellyfin prefix".</summary>
    public string PrefixMappings { get; set; } = string.Empty;

    /// <summary>Remembered state of the "Skip duplicate tracks" checkbox.</summary>
    public bool Deduplicate { get; set; } = true;

    /// <summary>Remembered state of the "match by file name alone" checkbox.</summary>
    public bool AllowNameOnly { get; set; } = true;
}
