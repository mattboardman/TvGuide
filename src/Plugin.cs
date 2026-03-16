#nullable enable

using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TvGuide;

public class Plugin : BasePlugin<TvGuideConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "TvGuide";

    public override string Description => "Virtual TV channels from library genres with weekly schedules";

    public override Guid Id => new Guid("b7e3c1d4-5f2a-4890-abcd-9e8f76543210");
}
