using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void SelectedOutputsKeepTheirLastDescriptionWhileAwayAndAcrossASettingsReload()
    {
        const string headset = "labels_headset", speakers = "labels_speakers";
        var pw = new PipeWireAdapter();
        var outputs = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint headsetModule = outputs.CreateNullSink(headset, "Labels headset");
        outputs.CreateNullSink(speakers, "Labels speakers");
        try
        {
            mixer.Build(MonitorConfig());
            Assert.True(SpinWait.SpinUntil(() => pw.ListDevices().Any(d => d.Name == speakers), TimeSpan.FromSeconds(3)));
            mixer.SetMonitorOutputs([headset]);
            mixer.SyncDeviceList();
            Assert.True(mixer.SyncMonitorOutputLabels());
            Assert.False(mixer.SyncMonitorOutputLabels());
            // Only the selected output is remembered.
            Assert.Equal(new Dictionary<string, string> { [headset] = "Labels headset" }, mixer.Snapshot().MonitorOutputLabels);

            // Unplugged: still selected, still named.
            outputs.UnloadModule(headsetModule);
            Assert.True(SpinWait.SpinUntil(() => !pw.ListDevices().Any(d => d.Name == headset), TimeSpan.FromSeconds(3)));
            Assert.True(mixer.SyncDeviceList());
            Assert.False(mixer.SyncMonitorOutputLabels());
            Assert.Equal([headset], mixer.Snapshot().MonitorOutputs);
            Assert.Equal("Labels headset", mixer.Snapshot().MonitorOutputLabels[headset]);
            MixerSettings saved = mixer.ExportSettings();
            Assert.Equal("Labels headset", saved.MonitorOutputLabels[headset]);

            // Deselecting an output forgets its name; the new one is learned.
            mixer.SetMonitorOutputs([speakers]);
            Assert.Empty(mixer.Snapshot().MonitorOutputLabels);
            Assert.True(mixer.SyncMonitorOutputLabels());
            Assert.Equal(new Dictionary<string, string> { [speakers] = "Labels speakers" }, mixer.Snapshot().MonitorOutputLabels);

            // A reload restores the saved names of the saved selection only,
            // and a file written before the field selects without a name.
            mixer.ApplySettings(saved with
            {
                MonitorOutputLabels = new() { [headset] = "Labels headset", ["labels_other"] = "Other" },
            });
            Assert.Equal(new Dictionary<string, string> { [headset] = "Labels headset" }, mixer.Snapshot().MonitorOutputLabels);
            mixer.ApplySettings(saved with { MonitorOutputLabels = [] });
            Assert.Equal([headset], mixer.Snapshot().MonitorOutputs);
            Assert.Empty(mixer.Snapshot().MonitorOutputLabels);
        }
        finally { outputs.TearDown(); }
    }
}
