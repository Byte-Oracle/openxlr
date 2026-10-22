using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class SoundCheckIntegrationTests
{
    [MonitorPipeWireFact]
    public void CombinedPauseStopsSoundCheckAndRefusesAnotherRecordingUntilMicrophonesResume()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = BuildCapture(pw, out _);
        mixer.SoundCheck("capture", "record");
        mixer.SetMonitorMuteMicrophones(true);
        Assert.Equal("capture", mixer.SoundCheckChannel);
        mixer.SetMonitorMuted(true);
        Assert.Null(mixer.SoundCheckChannel);
        Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
        Assert.True(pw.GetSinkMuted("OpenXLR_ch_capture"));
        Assert.Contains("Resume monitoring", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("capture", "record")).Message);
        mixer.SoundCheck("capture", "stop");
        mixer.SetMonitorMuteMicrophones(false);
        mixer.SoundCheck("capture", "record");
        Assert.Equal("capture", mixer.SoundCheckChannel);
        mixer.SoundCheck("capture", "stop");
    }
}
