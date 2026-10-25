namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public Task AClipGuardTakeStaysRawAndRecordingLoopingAndStopKeepAudioFlowing()
        => SoundCheckIntegrationTests.CheckMicrophoneLoop(clipGuard: true);
}
