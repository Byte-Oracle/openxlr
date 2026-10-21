using System.Reflection;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class DuckingHandOverTests
{
    private const string Pro = "0fd9:00b4@00112233445566aa";

    private sealed class Port : IDeviceDuckingPort
    {
        public DeviceDuckingStatus Current = DeviceDuckingStatus.None;
        public ProDuckingTarget? Held;
        public readonly List<ProDuckingTarget> Posts = [];
        public Action<ProDuckingTarget>? OnPost;
        // The device side drops posts for a unit it no longer drives.
        public bool Dropping;

        public void Post(string unit, ProDuckingTarget target, string owner)
        {
            Assert.Equal(Pro, unit);
            Assert.False(string.IsNullOrWhiteSpace(owner));
            OnPost?.Invoke(target);
            Posts.Add(target);
            if (!Dropping) Current = new(DeviceDuckingPhase.Pending, target);
        }

        public DeviceDuckingStatus Status(string unit) => Current;

        public void Sweep()
        {
            Held = Current.Value;
            Current = new(DeviceDuckingPhase.Verified, Held);
        }
    }

    private static Mixer Create(Port port, ManualTime? time = null, bool enabled = true)
    {
        var mixer = new Mixer(new PipeWireAdapter { DuckHost = "/missing-openxlr-duck-host" }) { Time = time ?? new ManualTime() };
        mixer.SetInputJacks(2, true);
        mixer.SetDrivenUnit(Pro);
        mixer.SetInputDeviceHint("Wave_XLR_Pro", hardwareOutputRouting: true);
        Field<List<string>>(mixer, "_monitorOutputs").Add("alsa_output.usb-Wave_XLR_Pro-00.pro-output-0#hp1");
        mixer.SetDeviceDuckingPort(port);
        mixer.SetUnitDuckingSupport(Pro, DuckingPlacementTests.Pro);
        mixer.SetDuckingSetup(DuckingSetup.Default(MixerConfig.Default()) with { Enabled = enabled }, _ => null);
        return mixer;
    }

    private static T Field<T>(Mixer mixer, string name)
        => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;

    private static DuckingPlan? Plan(Mixer mixer) => Field<DuckingPlan?>(mixer, "_duckPlan");

    private static void Set(Mixer mixer, Func<DuckingSetup, DuckingSetup> change)
        => mixer.SetDuckingSetup(change(mixer.DuckingSnapshot().Setup), _ => null);

    [Fact]
    public void DeviceToSoftwareWaitsForVerifiedOff()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        port.Sweep();
        Assert.True(port.Held!.Enabled);
        Assert.True(port.Held.Pairs.SetEquals([ProReturnPairs.Monitor]));
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Set(mixer, s => s with { Processing = "software" });
        Assert.False(port.Posts[^1].Enabled);
        Assert.Null(Plan(mixer));
        mixer.SyncDucking();
        Assert.Null(Plan(mixer));
        port.Sweep();
        mixer.SyncDucking();
        Assert.False(port.Held.Enabled);
        Assert.True(Plan(mixer)!.Enabled);
        Assert.DoesNotContain(Plan(mixer)!.Cells, c => c.StartsWith("xlr1|", StringComparison.Ordinal));
        Assert.True(mixer.DuckingSnapshot().Device!.Ready);
        Assert.Null(mixer.DuckingSnapshot().Backend); // No graph in this test, so no running software stage.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingOrFailedOffWaitsForTheOneSecondDeadline(bool failed)
    {
        var port = new Port();
        var time = new ManualTime();
        using Mixer mixer = Create(port, time);
        port.Sweep();
        Set(mixer, s => s with { Processing = "software" });
        if (failed) port.Current = new(DeviceDuckingPhase.Failed);
        time.Advance(Mixer.DuckingReleaseWait - TimeSpan.FromMilliseconds(1));
        mixer.SyncDucking();
        Assert.Null(Plan(mixer));
        time.Advance(TimeSpan.FromMilliseconds(1));
        mixer.SyncDucking();
        Assert.True(Plan(mixer)!.Enabled);
    }

    [Fact]
    public void SoftwareToDeviceRemovesThePlanBeforePosting()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        Set(mixer, s => s with { Processing = "software" });
        port.Sweep();
        mixer.SyncDucking();
        Assert.NotNull(Plan(mixer));
        port.OnPost = target => { if (target.Enabled) Assert.Null(Plan(mixer)); };
        Set(mixer, s => s with { Processing = "device" });
        Assert.True(port.Posts[^1].Enabled);
        Assert.Null(Plan(mixer));
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
    }

    [Fact]
    public void AFailedSaveLeavesBothBackendsAlone()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        int posts = port.Posts.Count;
        Assert.Throws<IOException>(() => mixer.SetDuckingSetup(mixer.DuckingSnapshot().Setup with { Processing = "software" }, _ => "read only"));
        Assert.Equal(posts, port.Posts.Count);
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Set(mixer, s => s with { Processing = "software" });
        port.Sweep();
        mixer.SyncDucking();
        DuckingPlan plan = Plan(mixer)!;
        posts = port.Posts.Count;
        Assert.Throws<IOException>(() => mixer.SetDuckingSetup(mixer.DuckingSnapshot().Setup with { Processing = "device" }, _ => "read only"));
        Assert.Same(plan, Plan(mixer));
        Assert.Equal(posts, port.Posts.Count);
    }

    [Fact]
    public void TogglingEnabledKeepsTheBackendAndOnlyChangesTheTargetOrPlan()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        int posts = port.Posts.Count;
        Set(mixer, s => s with { Enabled = false });
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Assert.False(port.Posts[^1].Enabled);
        Set(mixer, s => s with { Enabled = true });
        Assert.Equal(posts + 2, port.Posts.Count);
        mixer.SyncDucking();
        Assert.Equal(posts + 2, port.Posts.Count);
        Set(mixer, s => s with { Processing = "software" });
        port.Sweep();
        mixer.SyncDucking();
        posts = port.Posts.Count;
        DuckingPlan before = Plan(mixer)!;
        Set(mixer, s => s with { Enabled = false });
        Assert.False(Plan(mixer)!.Enabled);
        Assert.True(before.Cells.SetEquals(Plan(mixer)!.Cells));
        Set(mixer, s => s with { Enabled = true, Amount = "high", Speed = "fast" });
        Assert.True(Plan(mixer)!.Enabled);
        Assert.Equal((24.0, 30.0, 200.0), (Plan(mixer)!.DepthDb, Plan(mixer)!.AttackMs, Plan(mixer)!.ReleaseMs));
        Assert.Equal(posts, port.Posts.Count);
    }

    [Fact]
    public void DeviceFailureStaysInSoftwareUntilAnOffToOnToggle()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        port.Current = new(DeviceDuckingPhase.Failed, Reason: "two misses");
        mixer.SyncDucking();
        Assert.Equal("failed", mixer.DuckingSnapshot().Device!.Reason);
        Assert.False(port.Posts[^1].Enabled);
        Assert.Null(Plan(mixer));
        port.Sweep();
        mixer.SyncDucking();
        Assert.True(Plan(mixer)!.Enabled);
        mixer.SyncDucking();
        Assert.NotEqual("device", mixer.DuckingSnapshot().Backend);
        Set(mixer, s => s with { Enabled = false });
        Set(mixer, s => s with { Enabled = true });
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Assert.True(mixer.DuckingSnapshot().Device!.Ready);
        Assert.Null(Plan(mixer));
    }

    [Fact]
    public void ADisappearingUnitCompletesReleaseWithoutWritingAndAReconnectIsPostedAgain()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        Set(mixer, s => s with { Processing = "software" });
        int posts = port.Posts.Count;
        port.Current = DeviceDuckingStatus.None;
        mixer.SetDrivenUnit(null);
        mixer.SetUnitDuckingSupport(null, null);
        Assert.Equal(posts, port.Posts.Count);
        Assert.True(Plan(mixer)!.Enabled);
        Assert.Null(mixer.DuckingSnapshot().Device);
        Set(mixer, s => s with { Processing = "device" });
        port.OnPost = target => { if (target.Enabled) Assert.Null(Plan(mixer)); };
        mixer.SetDrivenUnit(Pro);
        mixer.SetUnitDuckingSupport(Pro, DuckingPlacementTests.Pro);
        Assert.Equal(posts + 1, port.Posts.Count);
        port.Sweep();
        Assert.True(port.Held!.Enabled);
        port.Current = DeviceDuckingStatus.None;
        mixer.SyncDucking();
        Assert.Equal(posts + 2, port.Posts.Count);
    }

    [Fact]
    public void ASoftwareSetupClearsConnectionLeftoversBeforeStartingItsPlan()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        Set(mixer, s => s with { Processing = "software" });
        port.Sweep();
        mixer.SyncDucking();
        Assert.NotNull(Plan(mixer));
        port.Current = DeviceDuckingStatus.None;
        port.Held = port.Held! with { Enabled = true };
        mixer.SyncDucking();
        Assert.False(port.Posts[^1].Enabled);
        Assert.Null(Plan(mixer));
        port.Sweep();
        mixer.SyncDucking();
        Assert.NotNull(Plan(mixer));
        Assert.False(port.Held!.Enabled);
    }

    [Fact]
    public void ADroppedOffPostKeepsTheSoftwarePlanAcrossSweeps()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        Set(mixer, s => s with { Processing = "software" });
        port.Sweep();
        mixer.SyncDucking();
        DuckingPlan plan = Plan(mixer)!;
        // The unit left before the mixer heard of it: posts are dropped.
        port.Current = DeviceDuckingStatus.None;
        port.Dropping = true;
        for (int sweep = 0; sweep < 3; sweep++)
        {
            int posts = port.Posts.Count;
            mixer.SyncDucking();
            Assert.Equal(posts + 1, port.Posts.Count);
            Assert.False(port.Posts[^1].Enabled);
            Assert.Same(plan, Plan(mixer));
        }
    }

    [Fact]
    public void HardwareControlModeAndStoppingWriteNothing()
    {
        var port = new Port();
        using (var mixer = new Mixer())
        {
            mixer.SetDrivenUnit(Pro);
            mixer.SetUnitDuckingSupport(Pro, DuckingPlacementTests.Pro);
            mixer.SetDuckingSetup(DuckingSetup.Default(MixerConfig.Default()) with { Enabled = true }, _ => null);
            mixer.SyncDucking();
            Assert.Equal("mixerOff", mixer.DuckingSnapshot().Device!.Reason);
            Assert.Null(Plan(mixer));
            Assert.Empty(port.Posts);
        }
        Mixer running = Create(port);
        port.Sweep();
        int posts = port.Posts.Count;
        running.Dispose();
        Assert.Equal(posts, port.Posts.Count);
        Assert.True(port.Held!.Enabled);
    }

    [Fact]
    public void PlacementFollowsTheCarrierSendsAndConsumersOnTheSweep()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        mixer.SetInputBinding("xlr1", new SourceBinding("other-source", 0, 0));
        mixer.SyncDucking();
        Assert.Equal("keyNotSupported", mixer.DuckingSnapshot().Device!.Reason);
        port.Sweep();
        mixer.SyncDucking();
        Assert.NotNull(Plan(mixer));
        mixer.SetInputBinding("xlr1", null);
        mixer.SyncDucking();
        Assert.Equal("device", mixer.DuckingSnapshot().Backend);
        Set(mixer, s => s with { Channels = [.. s.Channels.Where(c => c != "voicechat")] });
        Assert.Equal("partialMix", mixer.DuckingSnapshot().Device!.Reason);
        Field<Dictionary<string, double>>(mixer, "_levels")["voicechat|monitor"] = 0;
        mixer.SyncDucking();
        Assert.True(mixer.DuckingSnapshot().Device!.Ready);
        Field<List<string>>(mixer, "_monitorOutputs").Add("speakers");
        mixer.SyncDucking();
        Assert.Equal("mixElsewhere", mixer.DuckingSnapshot().Device!.Reason);
        Assert.Equal(["monitor"], mixer.DuckingSnapshot().Device!.Outputs);
    }

    [Fact]
    public void DeviceTargetsUseDerivedPairsAndEmptyTargetsNeverEnableDucking()
    {
        var port = new Port();
        using Mixer mixer = Create(port, enabled: false);
        Set(mixer, s => s with { Enabled = true, Mixes = ["monitor", "auxout"],
            Channels = [.. MixerConfig.Default().Channels.Where(c => c.Id != "xlr1").Select(c => c.Id)] });
        // The default key is open in Aux, so the software send must be closed for a hardware fit.
        Field<Dictionary<string, double>>(mixer, "_levels")["xlr1|auxout"] = 0;
        mixer.SyncDucking();
        Assert.True(port.Posts[^1].Pairs.SetEquals([ProReturnPairs.Monitor, ProReturnPairs.Aux]));
        Set(mixer, s => s with { Mixes = [] });
        Assert.False(port.Posts[^1].Enabled);
        Assert.Empty(port.Posts[^1].Pairs);
    }

    [Fact]
    public void AnotherUnitsJacksAreAnExternalConsumerAndReadbackIsReportedWithoutAHelper()
    {
        var port = new Port();
        using Mixer mixer = Create(port);
        port.Sweep();
        var readback = new DuckingReadback("medium", "medium", "medium", [1], [5]);
        port.Current = port.Current with { Readback = readback };
        Assert.True(mixer.SyncDucking());
        DuckingStateSnapshot state = mixer.DuckingSnapshot();
        Assert.Equal("device", state.Backend);
        Assert.False(state.Available);
        Assert.Equal(Mixer.DuckingNeedsHost, state.Error);
        Assert.True(state.Device!.Held);
        Assert.Equal(readback, state.Device.Readback);
        Assert.False(mixer.SyncDucking());
        Field<List<string>>(mixer, "_monitorOutputs").Add("alsa_output.usb-Other_Pro-00.pro-output-0#hp1");
        mixer.SyncDucking();
        Assert.Equal("mixElsewhere", mixer.DuckingSnapshot().Device!.Reason);
        Assert.False(port.Posts[^1].Enabled);
    }

    [Theory]
    [InlineData(DeviceDuckingPhase.None, null, true)]
    [InlineData(DeviceDuckingPhase.Pending, false, false)]
    [InlineData(DeviceDuckingPhase.Failed, false, false)]
    [InlineData(DeviceDuckingPhase.Verified, true, false)]
    [InlineData(DeviceDuckingPhase.Verified, false, true)]
    public void OnlyVerifiedOffOrADroppedUnitCompletesRelease(DeviceDuckingPhase phase, bool? enabled, bool off)
    {
        ProDuckingTarget? target = enabled is { } on ? new(on, "medium", "medium", "medium", new HashSet<int> { 1 }) : null;
        Assert.Equal(off, new DeviceDuckingStatus(phase, target).IsOff);
    }
}
