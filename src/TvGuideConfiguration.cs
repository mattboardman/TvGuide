using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TvGuide;

public class GenreChannelOverride
{
    public string Genre { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> ExcludedItemIds { get; set; } = new();
}

public class CustomChannelDefinition
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> ItemIds { get; set; } = new();
}

public class TvGuideConfiguration : BasePluginConfiguration
{
    public const int DefaultVideoBitrateKbps = 4000;
    public const int DefaultAudioBitrateKbps = 384;
    public const int MinVideoBitrateKbps = 500;
    public const int MaxVideoBitrateKbps = 40000;
    public const int MinAudioBitrateKbps = 64;
    public const int MaxAudioBitrateKbps = 512;

    public int VideoBitrateKbps { get; set; } = DefaultVideoBitrateKbps;

    public int AudioBitrateKbps { get; set; } = DefaultAudioBitrateKbps;

    public int GetEffectiveVideoBitrateKbps()
        => Math.Clamp(VideoBitrateKbps, MinVideoBitrateKbps, MaxVideoBitrateKbps);

    public int GetEffectiveAudioBitrateKbps()
        => Math.Clamp(AudioBitrateKbps, MinAudioBitrateKbps, MaxAudioBitrateKbps);

    public List<GenreChannelOverride> GenreOverrides { get; set; } = new();

    public List<CustomChannelDefinition> CustomChannels { get; set; } = new();

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-5-mini";

    public int ScheduleEpoch { get; set; } = 0;
}
