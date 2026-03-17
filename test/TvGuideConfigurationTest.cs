using NUnit.Framework;

namespace Jellyfin.Plugin.TvGuide.Tests;

[TestFixture]
public class TvGuideConfigurationTest
{
    [Test]
    public void EffectiveBitrates_AreClampedToSupportedRanges()
    {
        var config = new TvGuideConfiguration
        {
            VideoBitrateKbps = 100,
            AudioBitrateKbps = 9999,
        };

        Assert.That(config.GetEffectiveVideoBitrateKbps(), Is.EqualTo(TvGuideConfiguration.MinVideoBitrateKbps));
        Assert.That(config.GetEffectiveAudioBitrateKbps(), Is.EqualTo(TvGuideConfiguration.MaxAudioBitrateKbps));
    }
}
