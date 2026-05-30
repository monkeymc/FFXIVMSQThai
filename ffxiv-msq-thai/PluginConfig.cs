using Dalamud.Configuration;
using System;

namespace FfxivMsqThai;

[Serializable]
public class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Base directory that contains the content_community/ folder.
    // Empty string = use the plugin's assembly directory (default for release).
    public string ContentRoot { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public float FontSize { get; set; } = 36f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
