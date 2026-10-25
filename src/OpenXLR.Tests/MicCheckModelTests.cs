using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>
/// Mic check's Shape and Position steps with no window: the four steps in
/// order, starting on Position; the presets are valid gentle EQs that the
/// manual lists as they are; a preset goes out as one setEq and its undo as
/// another; and the Position tips, phantom power and filters follow the unit
/// the channel carries.
/// </summary>
public sealed class MicCheckModelTests
{
    private sealed class Recorder : IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();

        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }

        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }

        public Task SetEqProcessingAsync(string channel, string value) { Sent.Enqueue(("setEqProcessing", channel, value)); return Task.CompletedTask; }

        public Task SetEqBandAsync(string channel, int band, EqBandChange change) { Sent.Enqueue(("setEqBand", channel, change)); return Task.CompletedTask; }

        public MicEq Eq()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Equal("setEq", sent.Command);
            Assert.Empty(Sent);
            return (MicEq)sent.Value;
        }
    }

    [Fact]
    public async Task TheStepsAreFourAndACheckStartsOnPosition()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var check = new MicCheckViewModel(client, "xlr1");
        Assert.Equal([MicCheckStep.Position, MicCheckStep.Level, MicCheckStep.Listen, MicCheckStep.Shape], check.Steps.Select(s => s.Step));
        Assert.Equal([Localizer.Text("MicCheckStepPosition"), Localizer.Text("MicCheckStepLevel"), Localizer.Text("MicCheckStepListen"),
            Localizer.Text("MicCheckStepShape")], check.Steps.Select(s => s.Name));
        Assert.True(check.ShowsPosition);
        check.Steps[3].IsSelected = true;
        Assert.True(check.ShowsShape);
        Assert.False(check.ShowsPosition || check.ShowsLevel || check.ShowsListen);
        // The step shown cannot be unchosen.
        check.Steps[3].IsSelected = false;
        Assert.True(check.ShowsShape);
    }

    [Fact]
    public void ThePresetsAreGentleValidEqsWithALowCutFirst()
    {
        Assert.Equal(["clear", "warm", "broadcast"], MicCheckPresets.All.Select(p => p.Id));
        Assert.Equal(3, MicCheckPresets.All.Select(p => p.Name).Distinct().Count());
        foreach (MicCheckPreset preset in MicCheckPresets.All)
        {
            Assert.Null(MicEq.Check(preset.Eq));
            Assert.True(preset.Eq.Enabled);
            Assert.All(preset.Eq.Bands, b => Assert.True(b.Enabled));
            Assert.Equal(MicEq.LowCut, preset.Eq.Bands[0].Type);
            Assert.InRange(preset.Eq.Bands[0].Frequency, 60, 100);
            // A starting point, not a sound: no band moves more than 3 dB.
            Assert.All(preset.Eq.Bands.Skip(1), b => Assert.InRange(b.Gain, -3, 3));
            // Already on the daemon's grid, so what is sent is what is kept.
            Assert.Equal(preset.Eq, MicEq.Snap(preset.Eq));
            Assert.Equal(preset.Eq, preset.For(EqLimits.Instance));
            Assert.NotEqual(Localizer.Text("MicCheckPresetHint"), preset.Hint);
        }
        // A daemon with a narrower range gets the preset held inside it.
        EqLimits narrow = EqLimits.Instance with { Gain = new(-2, 2, 0.25), Frequency = new(100, 10000, 1) };
        MicEq held = MicCheckPresets.Broadcast.For(narrow);
        Assert.All(held.Bands, b => Assert.InRange(b.Gain, -2, 2));
        Assert.All(held.Bands, b => Assert.InRange(b.Frequency, 100, 10000));
    }

    /// <summary>The manual's table is the presets as the code holds them, band by band.</summary>
    [Fact]
    public void TheManualListsThePresetsAsTheyAre()
    {
        string manual = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "docs", "manual.md"));
        foreach (MicCheckPreset preset in MicCheckPresets.All)
        {
            string row = $"| {preset.Name} | " + string.Join(" | ", preset.Eq.Bands.Select(Describe)) + " |";
            Assert.Contains(row, manual);
        }

        static string Describe(EqBand b)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string hz = b.Frequency >= 1000 ? (b.Frequency / 1000).ToString("0.#", c) + " kHz" : b.Frequency.ToString("0", c) + " Hz";
            string db = b.Gain.ToString("+0.##;-0.##;0", c) + " dB";
            return b.Type switch
            {
                MicEq.LowCut => $"high pass {hz}",
                MicEq.LowShelf => $"low shelf {hz} {db}",
                MicEq.HighShelf => $"high shelf {hz} {db}",
                MicEq.Peak => $"bell {hz} {db}, Q {b.Q.ToString("0.00", c)}",
                _ => throw new InvalidOperationException(b.Type),
            };
        }
    }

    [Fact]
    public async Task APresetIsOneSetEqAndItsUndoPutsTheEqBackInOne()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var sent = new Recorder();
        using var eq = new MicEqViewModel(sent, "xlr1", "XLR 1", TimeSpan.Zero, new ManualTime());
        var check = new MicCheckViewModel(client, "xlr1");
        Assert.False(check.CanApplyPreset);
        check.ApplyPreset(MicCheckPresets.Clear);
        Assert.Empty(sent.Sent);

        check.Eq = eq;
        MicEq before = eq.Current;
        Assert.False(before.Enabled);
        Assert.True(check.CanApplyPreset);
        Assert.False(check.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicCheckPresetHint"), check.PresetStatus);

        check.ApplyPreset(MicCheckPresets.Clear);
        Assert.Equal(MicCheckPresets.Clear.Eq, sent.Eq());
        Assert.Equal(MicCheckPresets.Clear.Eq, eq.Current);
        Assert.True(eq.Enabled);
        Assert.True(check.CanUndoPreset);
        Assert.Equal(Localizer.Format("MicCheckPresetApplied", MicCheckPresets.Clear.Name), check.PresetStatus);

        // A second preset replaces the first; the undo goes back one step.
        check.ApplyPreset(MicCheckPresets.Broadcast);
        Assert.Equal(MicCheckPresets.Broadcast.Eq, sent.Eq());
        check.UndoPreset();
        Assert.Equal(MicCheckPresets.Clear.Eq, sent.Eq());
        Assert.Equal(MicCheckPresets.Clear.Eq, eq.Current);
        Assert.False(check.CanUndoPreset);
        Assert.Equal(Localizer.Text("MicCheckPresetUndone"), check.PresetStatus);
        check.UndoPreset();
        Assert.Empty(sent.Sent);

        // The EQ from before the first preset, switch included, comes back whole.
        check.ApplyPreset(MicCheckPresets.Warm);
        sent.Eq();
        check.Eq = null;
        check.Eq = eq;
        Assert.False(check.CanUndoPreset);
        check.ApplyPreset(MicCheckPresets.Warm);
        sent.Eq();
        check.Reset();
        Assert.False(check.CanUndoPreset);
        check.UndoPreset();
        Assert.Empty(sent.Sent);
        eq.Replace(before);
        sent.Eq();
        check.ApplyPreset(MicCheckPresets.Warm);
        sent.Eq();
        check.UndoPreset();
        Assert.Equal(before, sent.Eq());
        Assert.False(eq.Enabled);
    }

    [Fact]
    public async Task PositionAndTheFiltersFollowTheUnitTheChannelCarries()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        string Xlr1 = InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1));
        string Xlr2 = InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2));
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState($"{Xlr1},{Xlr2}"))!;
        state["mixer"]!["channels"]![0]!["eq"] = CaptureInputWindowTests.CaptureEq(enabled: true);
        CaptureInputWindowTests.Apply(main, state);
        MicCheckViewModel first = main.Inserts.MicCheck, second = main.Inserts2.MicCheck;
        ChannelViewModel xlr1 = main.Channels.Single(c => c.Id == "xlr1");

        // The Pro's XLR inputs: the unit by name and input, its phantom power,
        // its own low cut and ClipGuard, and XLR 1's EQ and software stage.
        Assert.Equal(MicCheckUnitKind.XlrJack, first.UnitKind);
        Assert.Equal(Localizer.Format("MicCheckPositionXlr", "Elgato Wave XLR Pro", 1), first.PositionIntro);
        Assert.Equal(Localizer.Format("MicCheckPositionXlr", "Elgato Wave XLR Pro", 2), second.PositionIntro);
        Assert.Equal(Localizer.Text("MicCheckTipXlrAddress"), first.PositionTips[0]);
        Assert.Contains(Localizer.Text("MicCheckTipAngle"), first.PositionTips);
        Assert.True(first.ShowsPhantom && first.HasPhantom && !first.PhantomElsewhere);
        Assert.True(first.HasUnitFilters);
        Assert.Same(main, first.Software);
        Assert.Null(second.Software);
        Assert.Same(xlr1.Eq, first.Eq);
        Assert.NotNull(first.Eq);
        Assert.Same(main.Inserts, first.Chain);
        Assert.Same(main.Inserts2, second.Chain);

        // A Wave:3's capsule: its own tips, and no phantom power to speak of.
        state["capabilities"]!["xlrJacks"] = false;
        state["capabilities"]!["phantom"] = false;
        state["capabilities"]!["lowCut"] = false;
        state["capabilities"]!["clipGuard"] = false;
        state["device"]!["model"] = "Wave:3";
        CaptureInputWindowTests.Apply(main, state);
        Assert.Equal(MicCheckUnitKind.Capsule, first.UnitKind);
        Assert.Equal(Localizer.Format("MicCheckPositionCapsule", "Elgato Wave:3"), first.PositionIntro);
        Assert.Equal([Localizer.Text("MicCheckTipCapsuleFront"), Localizer.Text("MicCheckTipCapsuleRoom")], first.PositionTips.Take(2));
        Assert.False(first.ShowsPhantom);
        Assert.False(first.HasUnitFilters);

        // XLR 1 bound to a webcam, with the Pro's microphone on a capture
        // channel: XLR 1 now carries no unit, and the capture channel does.
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingModelTests.ProState(
            $"{InputBindingModelTests.BoundXlr1(true)},{Xlr2},{InputBindingModelTests.ProMic(InputBindingModelTests.Input(1))}",
            mixerExtra: ",\"softLowCutApplicable\":true,\"softClipGuardApplicable\":true"))!);
        Assert.Equal(MicCheckUnitKind.None, first.UnitKind);
        Assert.Equal(Localizer.Text("MicCheckPositionNone"), first.PositionIntro);
        Assert.Equal(Localizer.Text("MicCheckTipOtherDevice"), first.PositionTips[0]);
        Assert.True(first.ShowsPhantom && !first.HasPhantom && first.PhantomElsewhere);
        Assert.False(first.HasUnitFilters);
        // XLR 1's software low cut and ClipGuard stay with XLR 1 whatever feeds it.
        Assert.True(first.HasSoftwareFilters);
        Assert.False(first.NoFilters);
        Assert.Null(first.Eq);
    }
}
