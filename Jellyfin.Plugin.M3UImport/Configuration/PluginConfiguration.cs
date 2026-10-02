using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.M3UImport.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>One mapping per line: "m3u prefix =&gt; Jellyfin prefix".</summary>
    public string PrefixMappings { get; set; } = string.Empty;
}
