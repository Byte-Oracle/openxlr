using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Devices;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The Wave XLR Pro's headphone-mix bits as the device manager drives them:
/// a write only when the unit holds something else, and the return pair left
/// as the unit holds it when the caller has no jack in use. The manager reads
/// gain locks and daemon settings from the configuration directory, which
/// these tests redirect.
/// </summary>
[Collection("xdg-config")]
public sealed class HeadphoneMixTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-headphone-mix-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public HeadphoneMixTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A Wave XLR Pro that counts the headphone-mix writes it takes.</summary>
    private sealed class Pro(bool monitorReturn, bool micDirect) : IAudioDevice
    {
        public bool MonitorReturn = monitorReturn, MicDirect = micDirect;
        public int ReturnWrites, DirectWrites;
        public DeviceInfo Info { get; } = new("Elgato", "Wave XLR Pro", 0x0fd9, 0x00b4);
        public DeviceCapabilities Capabilities { get; } = new() { Gain = true, OutputRouting = true, RetainsSettings = true };
        public bool Connected { get; private set; }
        public void Connect() => Connected = true;
        public void Disconnect() => Connected = false;
        public void Dispose() { }
        public DeviceState ReadState() => new() { GainDb = 30, HpMixMonitorReturn = MonitorReturn, HpMixMicDirect = MicDirect };
        public void SetHpMixMonitorReturn(bool on) { ReturnWrites++; MonitorReturn = on; }
        public void SetHpMixMicDirect(bool on) { DirectWrites++; MicDirect = on; }
        public void SetGainDb(int db) { } public void SetMute(bool on) { } public void SetLowCut(bool on) { }
        public void SetExpander(bool on) { } public void SetVoiceTune(bool on) { } public void SetVoiceTuneStrength(int v) { }
        public void SetHpVolumeDb(double db) { } public void SetLowImpedance(bool on) { } public void SetCrossfade(int v) { }
        public void SetPhantom(bool on) { } public void SetClipGuard(bool on) { } public void SetCompressor(bool on) { }
    }

    private static DeviceManager Connect(Pro unit)
    {
        var manager = new DeviceManager(NullLogger<DeviceManager>.Instance, new ConfigurationBuilder().Build(), () => [unit]);
        manager.SweepOnce();
        Assert.True(unit.Connected);
        return manager;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADirectPathLeftOnIsClearedOnceAndTheReturnPairIsLeftAlone(bool monitorReturn)
    {
        var unit = new Pro(monitorReturn, micDirect: true);
        using DeviceManager manager = Connect(unit);

        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
        Assert.Equal((monitorReturn, 0), (unit.MonitorReturn, unit.ReturnWrites));
        Assert.False(manager.Snapshot().State!.HpMixMicDirect);

        // Later sweeps find it off: nothing is written, so the playback
        // stream is not restarted again.
        for (int i = 0; i < 3; i++)
        {
            manager.SweepOnce();
            Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        }
        Assert.Equal((1, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void APathAlreadyOffIsNotWritten()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.Equal((0, 0), (unit.DirectWrites, unit.ReturnWrites));
    }

    [Fact]
    public void AnotherProgramTurningThePathOnAgainIsUndoneOnTheNextSweep()
    {
        var unit = new Pro(monitorReturn: true, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        unit.MicDirect = true;   // written by something outside OpenXLR
        manager.SweepOnce();
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: null, micDirect: false));
        Assert.False(unit.MicDirect);
        Assert.Equal(1, unit.DirectWrites);
    }

    [Fact]
    public void WithAJackInUseTheReturnPairIsAssertedAndThePathFollowsTheMixer()
    {
        var unit = new Pro(monitorReturn: false, micDirect: false);
        using DeviceManager manager = Connect(unit);
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true));
        Assert.Equal((true, true), (unit.MonitorReturn, unit.MicDirect));
        Assert.False(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: true));
        Assert.True(manager.EnsureHeadphoneMix(monitorReturn: true, micDirect: false));
        Assert.Equal((true, false), (unit.MonitorReturn, unit.MicDirect));
        Assert.Equal((1, 2), (unit.ReturnWrites, unit.DirectWrites));
    }
}
