using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void VirtualMicrophoneKeepsBothEffectChainsWhenTheRecordingApplicationRestarts()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("effects_signal", "Microphone signal");
        pw.CreateVirtualMic("Wave_XLR_EFFECTS_source", "effects_signal.monitor", "Synthetic Wave XLR");
        mixer.SetInputDeviceHint(["Wave_XLR_EFFECTS_source"], hardwareOutputRouting: false, modelFallback: false);
        var effects = new MixDefinition("effects", "Mic Effects", MixKind.VirtualMic);
        mixer.Build(new MixerConfig
        {
            Channels = [new("xlr1", "XLR 1") { InputPair = 0, Levels = new Dictionary<string, double> { ["effects"] = 1 } }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), effects],
        });
        CaptureChainFixture.Wait(() =>
        {
            mixer.EnsureInputFeeds();
            return mixer.Snapshot().Channels.Single().BindingConnected;
        });
        int? device = pw.FindNodeId(effects.VirtualMicName);
        Assert.NotNull(device);
        mixer.SetInserts("xlr1", [CaptureChainFixture.Gain(1, .25)]);
        mixer.SetInserts("mix:effects", [CaptureChainFixture.Gain(2, .5)]);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Error);
        Assert.Null(Assert.Single(mixer.Snapshot().Inserts["mix:effects"]).Error);
        // Each call opens and closes a PulseAudio recording client, as
        // Discord or OBS does. Both effects must remain in its audio path.
        for (int restart = 0; restart < 2; restart++)
            CaptureLevel(.0125, "effects_signal", effects.VirtualMicName, virtualMicrophone: true);
        mixer.SetInsertBypass("xlr1", "gain", true);
        CaptureLevel(.05, "effects_signal", effects.VirtualMicName, virtualMicrophone: true);
        mixer.SetInsertBypass("mix:effects", "gain", true);
        CaptureLevel(.1, "effects_signal", effects.VirtualMicName, virtualMicrophone: true);
        Assert.Equal(device, pw.FindNodeId(effects.VirtualMicName));
    }
}
