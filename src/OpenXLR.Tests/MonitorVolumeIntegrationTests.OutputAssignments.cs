using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void MultipleOutputsShareCustomMonitorFeedsAcrossRecallAndOutputReconnection()
    {
        const string headset = "assignment_headset", speakers = "assignment_speakers";
        var pw = new PipeWireAdapter();
        var outputs = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint? phonesModule = outputs.CreateNullSink(headset, "Assignment headset");
        uint speakersModule = outputs.CreateNullSink(speakers, "Assignment speakers");
        try
        {
            mixer.Build(MonitorConfig());
            mixer.CreateMix("Desk", _ => null, MixKind.Monitor);
            mixer.CreateMix("Voice", _ => null, MixKind.Monitor);
            mixer.SetMonitorOutputs([headset, speakers]);
            Assert.Null(mixer.SetMonitorFeed(headset, "voice+desk"));
            Assert.Null(mixer.SetMonitorFeed(speakers, "desk"));
            AssertRoutes(headset, "desk", "voice");
            AssertRoutes(speakers, "desk");

            MixerSettings saved = mixer.ExportSettings();
            mixer.RenameMix("voice", "Renamed voice", _ => null);
            Assert.Equal("desk+voice", mixer.MonitorFeedOf(headset));
            mixer.Build(MixerConfig.FromSettings(saved));
            mixer.ApplySettings(saved);
            AssertRoutes(headset, "desk", "voice");
            AssertRoutes(speakers, "desk");

            outputs.UnloadModule(phonesModule.Value);
            phonesModule = null;
            Assert.True(SpinWait.SpinUntil(() => !MixLinks().Any(link => link.To.StartsWith(headset + ":", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)));
            mixer.EnsureMonitorRoutes();
            Assert.Equal([headset, speakers], mixer.MonitorOutputs);
            Assert.Equal("desk+voice", mixer.MonitorFeedOf(headset));
            AssertRoutes(speakers, "desk");
            phonesModule = outputs.CreateNullSink(headset, "Assignment headset returned");
            AssertRoutes(headset, "desk", "voice");
            AssertRoutes(speakers, "desk");

            Assert.NotNull(mixer.SetMonitorFeed(headset, ""));
            Assert.NotNull(mixer.SetMonitorFeed(headset, "desk+desk"));
            Assert.NotNull(mixer.SetMonitorFeed(headset, "desk+missing"));
            AssertRoutes(headset, "desk", "voice");
            mixer.DeleteMix("voice", _ => null);
            AssertRoutes(headset, "desk");
            AssertRoutes(speakers, "desk");

            void AssertRoutes(string output, params string[] mixIds)
            {
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureMonitorRoutes();
                    var links = MixLinks().Where(link => link.To.StartsWith(output + ":", StringComparison.Ordinal)).ToList();
                    var sources = links.Select(link => link.From.Split(':')[0]).ToHashSet();
                    return links.Count == mixIds.Length * 2 && sources.SetEquals(mixIds.Select(id => "OpenXLR_mix_" + id));
                }, TimeSpan.FromSeconds(5)), "The output did not receive exactly both stereo sides of its selected mixes.");
            }
        }
        finally
        {
            if (phonesModule is uint module) outputs.UnloadModule(module);
            outputs.UnloadModule(speakersModule);
        }
    }
}
