using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Which hardware strips the daemon tells clients to show. Only the Wave XLR
/// Pro has a second XLR jack and the auxiliary input stage, so on every other
/// model those channels can never carry audio. They stay in the layout and
/// keep their levels; the state says the device cannot feed them.
/// </summary>
public sealed class ChannelPresenceTests
{
    private static Dictionary<string, bool> Present(Mixer mixer)
        => mixer.Snapshot().Channels.ToDictionary(c => c.Id, c => c.Present);

    [Fact]
    public void AChannelTheDeviceCannotFeedIsReportedAbsent()
    {
        using var mixer = new Mixer();
        Assert.True(mixer.SetInputJacks(1, false));
        Dictionary<string, ChannelStatus> single = mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.True(single["xlr1"].Present);
        Assert.False(single["xlr2"].Present);
        Assert.False(single["aux"].Present);
        // An application channel has no jack behind it and is never absent.
        Assert.True(single["game"].Present);
        // The strip is still there, so its sends survive the device change.
        Assert.NotEmpty(single["xlr2"].Levels);

        Assert.True(mixer.SetInputJacks(2, true));
        Dictionary<string, ChannelStatus> pro = mixer.Snapshot().Channels.ToDictionary(c => c.Id);
        Assert.True(pro["xlr2"].Present);
        Assert.True(pro["aux"].Present);
    }

    [Fact]
    public void BeforeAnyUnitConnectsOnlyXlr1ShowsUnlessAUnitIsAttached()
    {
        using var mixer = new Mixer();
        // A desk with no Elgato unit gets one strip, not three.
        Dictionary<string, bool> bare = Present(mixer);
        Assert.True(bare["xlr1"]);
        Assert.False(bare["xlr2"]);
        Assert.False(bare["aux"]);
        Assert.True(bare["game"]);
        // A Pro attached but not driven yet (boot, a failed claim) keeps its strips.
        Assert.True(mixer.SetAttached(true));
        Assert.False(mixer.SetAttached(true));
        Assert.All(Present(mixer).Values, Assert.True);
        Assert.True(mixer.SetAttached(false));
        Assert.False(Present(mixer)["aux"]);
    }

    [Fact]
    public void WithNoUnitConnectedTheLastAnswerOfThisRunIsKept()
    {
        using var mixer = new Mixer();
        mixer.SetAttached(true);
        Assert.True(mixer.SetInputJacks(2, true));
        // Unplugged: the Pro's strips stay for its return.
        Assert.True(mixer.SetInputJacks(null, null));
        mixer.SetAttached(false);
        Assert.All(Present(mixer).Values, Assert.True);
        // A one-jack unit came and went: its answer is the one kept now.
        mixer.SetInputJacks(1, false);
        mixer.SetInputJacks(null, null);
        mixer.SetAttached(true);
        Dictionary<string, bool> kept = Present(mixer);
        Assert.False(kept["xlr2"]);
        Assert.False(kept["aux"]);
    }

    [Fact]
    public void AStripBoundToASourceIsAlwaysPresent()
    {
        using var mixer = new Mixer();
        mixer.SetInputJacks(1, false);
        Assert.False(Present(mixer)["xlr2"]);
        mixer.SetInputBinding("xlr2", new("alsa_input.usb-cam", 0, 1));
        Assert.True(Present(mixer)["xlr2"]);
        mixer.SetInputJacks(null, null);
        Assert.True(Present(mixer)["xlr2"]);
        mixer.SetInputBinding("xlr2", null);
        Assert.False(Present(mixer)["xlr2"]);
    }

    [Fact]
    public void OnlyAChangedAnswerAsksForABroadcast()
    {
        using var mixer = new Mixer();
        Assert.True(mixer.SetInputJacks(1, false));
        Assert.False(mixer.SetInputJacks(1, false));
        Assert.True(mixer.SetInputJacks(null, null));
        Assert.False(mixer.SetInputJacks(null, null));
    }
}
