using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using DaemonBand = OpenXLR.Core.Mixing.EqBand;
using DaemonEq = OpenXLR.Core.Mixing.MicEq;
using DaemonResponse = OpenXLR.Core.Mixing.EqResponse;

namespace OpenXLR.Tests;

/// <summary>
/// The EQ editor's model, with no window: the curve it draws is the daemon's
/// own <c>EqResponse</c> at every point, a position on the graph maps to a
/// frequency on the log axis and a gain, both snapped on the daemon's steps
/// and held to its ranges; a wheel notch is one step of the Q grid; a type
/// without a gain keeps its gain and takes the gain field away; values the
/// daemon reports are what the editor shows, unless they are an older echo of
/// an edit; band changes go out at most once a send interval per band,
/// merged and never losing the last; the comparison switches the EQ off and
/// on while the switch stays on; copy and paste and reset send one setEq;
/// and the window's model makes an EQ only for a channel the daemon reports
/// one for.
/// </summary>
[Collection("xdg-config")]
public sealed class EqEditorModelTests
{
    /// <summary>The three commands as the editor sends them.</summary>
    private sealed class Recorder : IEqCommands
    {
        public readonly ConcurrentQueue<(string Command, string Channel, object Value)> Sent = new();

        public Task SetEqAsync(string channel, MicEq eq) { Sent.Enqueue(("setEq", channel, eq)); return Task.CompletedTask; }

        public Task SetEqEnabledAsync(string channel, bool enabled) { Sent.Enqueue(("setEqEnabled", channel, enabled)); return Task.CompletedTask; }

        public Task SetEqBandAsync(string channel, int band, EqBandChange change)
        {
            Sent.Enqueue(("setEqBand", channel, (band, change)));
            return Task.CompletedTask;
        }

        public (int Band, EqBandChange Change) Band()
        {
            Assert.True(Sent.TryDequeue(out var sent), "nothing was sent");
            Assert.Equal("setEqBand", sent.Command);
            Assert.Empty(Sent);
            return ((int, EqBandChange))sent.Value;
        }
    }

    private static (MicEqViewModel Eq, Recorder Sent) Model(string channel = "xlr1")
    {
        var sent = new Recorder();
        return (new MicEqViewModel(sent, channel, channel == "xlr1" ? "XLR 1" : "XLR 2", TimeSpan.Zero), sent);
    }

    private static JsonNode State(bool enabled, string bands, string backend = "\"software\"") => JsonNode.Parse(
        $$"""{"enabled":{{(enabled ? "true" : "false")}},"bands":[{{bands}}],"backend":{{backend}},"available":true,"error":null}""")!;

    private const string Defaults = """
        {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
        {"enabled":true,"type":"peak","frequency":310,"gain":0,"q":0.7046930689671475},
        {"enabled":true,"type":"peak","frequency":2172,"gain":0,"q":0.7046930689671475},
        {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
        """;

    private static readonly Rect Plot = new(30, 6, 640, 300);

    [Fact]
    public void TheCurveIsTheDaemonsResponseAtEveryPointOnALogAxis()
    {
        EqBand[] bands =
        [
            new() { Enabled = true, Type = MicEq.LowCut, Frequency = 120, Q = MicEq.SnapQ(4) },
            new() { Enabled = true, Type = MicEq.Peak, Frequency = 1000, Gain = 9.25, Q = MicEq.SnapQ(2) },
            new() { Enabled = true, Type = MicEq.HighShelf, Frequency = 5000, Gain = -6, Q = MicEq.SnapQ(3) },
            new() { Enabled = false, Type = MicEq.Notch, Frequency = 3000, Q = MicEq.SnapQ(10) },
        ];
        DaemonBand[] daemon = [.. bands.Select(b => new DaemonBand { Enabled = b.Enabled, Type = b.Type, Frequency = b.Frequency, Gain = b.Gain, Q = b.Q })];
        Point[] sum = EqCurve.Sum(bands, Plot, 256);
        Assert.Equal(256, sum.Length);
        for (int i = 0; i < sum.Length; i++)
        {
            double hz = EqCurve.FrequencyAt(sum[i].X, Plot);
            Assert.Equal(EqCurve.FrequencyOf(i, 256), hz, 1e-6 * hz);
            // Evenly spaced on the axis: equal steps in log frequency.
            Assert.Equal(Plot.Left + Plot.Width * i / 255, sum[i].X, 1e-9);
            double expected = DaemonResponse.CascadeDb(daemon, 48000, hz);
            Assert.Equal(EqCurve.Y(expected, Plot), sum[i].Y, 1e-6);
            Assert.Equal(expected, EqCurve.GainAt(sum[i].Y, Plot), 1e-6);
        }
        // Each band's own curve too, the switch of the EQ aside.
        for (int b = 0; b < bands.Length; b++)
        {
            Point[] curve = EqCurve.Band(bands[b], Plot, 64);
            var c = DaemonResponse.Coefficients(daemon[b], 48000);
            for (int i = 0; i < curve.Length; i++)
                Assert.Equal(EqCurve.Y(DaemonResponse.MagnitudeDb(c, 48000, EqCurve.FrequencyOf(i, 64)), Plot), curve[i].Y, 1e-6);
        }
        // The axis: 20 Hz at the left edge, 20 kHz at the right, 1 kHz two thirds in; +12 dB at the top.
        Assert.Equal(Plot.Left, EqCurve.X(20, Plot), 1e-9);
        Assert.Equal(Plot.Right, EqCurve.X(20000, Plot), 1e-9);
        Assert.Equal(Plot.Left + Plot.Width * 2 / 3, EqCurve.X(2000, Plot), 1e-9);
        Assert.Equal(Plot.Top, EqCurve.Y(12, Plot), 1e-9);
        Assert.Equal(Plot.Center.Y, EqCurve.Y(0, Plot), 1e-9);
        Assert.Equal(["20", "80", "300", "1.4k", "5k", "12k", "20k"], EqCurve.Edges.Select(EqCurve.Label));
        // A gainless band's handle sits on the 0 dB line, whatever gain it keeps.
        Assert.Equal(EqCurve.Y(0, Plot), EqCurve.Handle(bands[0] with { Gain = 9 }, Plot).Y);
        Assert.Equal(EqCurve.Y(9.25, Plot), EqCurve.Handle(bands[1], Plot).Y);
    }

    [Fact]
    public void ADragMapsToFrequencyAndGainOnTheDaemonsSteps()
    {
        var (eq, sent) = Model();
        EqBandViewModel band = eq.Bands[1];
        // Two thirds across is 2 kHz; a quarter down is +6 dB.
        band.BeginDrag();
        Assert.Same(band, eq.SelectedBand);
        Point at = new(Plot.Left + Plot.Width * 2 / 3 + 0.3, Plot.Top + Plot.Height / 4 + 0.4);
        band.DragTo(EqCurve.FrequencyAt(at.X, Plot), EqCurve.GainAt(at.Y, Plot));
        var (number, change) = sent.Band();
        Assert.Equal(2, number);
        double hz = Math.Round(EqCurve.FrequencyAt(at.X, Plot));
        Assert.InRange(hz, 2001, 2012);
        Assert.Equal(hz, change.Frequency);
        Assert.Equal(6, change.Gain);
        Assert.Null(change.Q);
        Assert.Equal(hz, band.Frequency);
        Assert.Equal(6, band.Gain);
        // Past the graph: held to the ranges, never refused by the daemon.
        band.DragTo(EqCurve.FrequencyAt(Plot.Right + 50, Plot), EqCurve.GainAt(Plot.Bottom + 80, Plot));
        (_, change) = sent.Band();
        Assert.Equal(20000, change.Frequency);
        Assert.Equal(-12, change.Gain);
        // Only what moved goes out.
        band.DragTo(20000.2, -11.6);
        (_, change) = sent.Band();
        Assert.Null(change.Frequency);
        Assert.Equal(-11.5, change.Gain);
        band.DragTo(20000, -11.55);
        Assert.Empty(sent.Sent);
        band.EndDrag();

        // A type without a gain drags Q up and down instead, on the grid.
        EqBandViewModel low = eq.Bands[0];
        low.BeginDrag();
        low.DragToWithQ(80.4, 2);
        (number, change) = sent.Band();
        Assert.Equal(1, number);
        Assert.Equal(80, change.Frequency);
        Assert.Equal(DaemonEq.SnapQ(2), change.Q);
        Assert.Null(change.Gain);
        low.EndDrag();
    }

    [Fact]
    public void ReportedValuesAreShownAndAnOlderEchoWaitsForTheEdit()
    {
        var (eq, sent) = Model();
        eq.Apply(State(true, """
            {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
            {"enabled":true,"type":"peak","frequency":310,"gain":9.25,"q":1.4125375446227544},
            {"enabled":true,"type":"peak","frequency":2172,"gain":-3.5,"q":0.7046930689671475},
            {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
            """), null);
        EqBandViewModel band = eq.Bands[1];
        Assert.Equal(9.25, band.Gain);
        Assert.Equal(1.4125375446227544, band.Q);
        Assert.Equal("1.41", band.QText);
        Assert.Equal(9.25m, band.GainValue);
        Assert.Equal(1.41m, band.QValue);
        Assert.Equal("310 Hz", band.FrequencyText);
        Assert.Equal("+9.25 dB", band.GainText);
        Assert.Equal("Band 2, Bell, 310 Hz, +9.25 dB, Q 1.41", band.Description);
        Assert.Equal("Band 4, off, Low pass, 15 kHz, Q 0.70", eq.Bands[3].Description);
        Assert.True(eq.Enabled);
        Assert.True(eq.IsLit);
        Assert.Empty(sent.Sent);

        // An edit, then a report from before it: the edit stays until its echo comes.
        string Report(double gain) => """
            {"enabled":true,"type":"lowCut","frequency":36,"gain":0,"q":0.7046930689671475},
            {"enabled":true,"type":"peak","frequency":310,"gain":GAIN,"q":1.4125375446227544},
            {"enabled":true,"type":"peak","frequency":2172,"gain":-3.5,"q":0.7046930689671475},
            {"enabled":false,"type":"highCut","frequency":15049,"gain":0,"q":0.7046930689671475}
            """.Replace("GAIN", gain.ToString(System.Globalization.CultureInfo.InvariantCulture));
        band.Gain = 3;
        Assert.Equal(3, sent.Band().Change.Gain);
        eq.Apply(State(true, Report(9.25)), null);
        Assert.Equal(3, band.Gain);
        eq.Apply(State(true, Report(3)), null);
        Assert.Equal(3, band.Gain);
        // After the echo, the daemon's word is the editor's, whoever changed it.
        eq.Apply(State(true, Report(-2)), null);
        Assert.Equal(-2, band.Gain);
        // During a drag the hand wins; once it ends, a report after its last edit is shown.
        band.BeginDrag();
        band.DragTo(310, 4);
        sent.Band();
        eq.Apply(State(true, Report(-2)), null);
        Assert.Equal(4, band.Gain);
        band.EndDrag();
        eq.Apply(State(true, Report(4)), null);
        eq.Apply(State(true, Report(5)), null);
        Assert.Equal(5, band.Gain);
        Assert.Equal(2, band.Number);
    }

    [Fact]
    public void TheWheelTheKeysAndATypeChangeEachSendTheirOneChange()
    {
        var (eq, sent) = Model();
        EqBandViewModel band = eq.Bands[2];
        double q = band.Q;
        // One notch, one step of the grid: log10(Q) moves 0.016.
        band.StepQ(1);
        double up = DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + 0.016));
        Assert.Equal(up, sent.Band().Change.Q);
        Assert.Equal(up, band.Q);
        Assert.Equal(0.016, Math.Log10(band.Q) - Math.Log10(q), 9);
        band.StepQ(-1);
        Assert.Equal(q, sent.Band().Change.Q);
        // Held to the range at either end.
        band.StepQ(-500);
        Assert.Equal(0.1, sent.Band().Change.Q!.Value, 9);
        band.StepQ(-1);
        Assert.Empty(sent.Sent);
        band.StepQ(500);
        Assert.Equal(10, sent.Band().Change.Q!.Value, 9);

        // The arrows: a twelfth of an octave, or a quarter of that, and never less than a hertz.
        band.StepFrequency(1.0 / 12);
        Assert.Equal(DaemonEq.SnapFrequency(2172 * Math.Pow(2, 1.0 / 12)), sent.Band().Change.Frequency);
        eq.Bands[0].SetFrequency(20);
        sent.Band();
        eq.Bands[0].StepFrequency(1.0 / 48);
        Assert.Equal(21, sent.Band().Change.Frequency);
        band.StepGain(1);
        Assert.Equal(1, sent.Band().Change.Gain);
        band.StepGain(0.25);
        Assert.Equal(1.25, sent.Band().Change.Gain);

        // A type without a gain keeps the gain and takes the field away.
        Assert.True(band.UsesGain);
        band.TypeChoice = EqTypeChoice.Of(MicEq.Notch);
        var (number, change) = sent.Band();
        Assert.Equal(3, number);
        Assert.Equal(new EqBandChange(Type: "notch"), change);
        Assert.False(band.UsesGain);
        Assert.Equal(1.25, band.Gain);
        Assert.Equal("Band 3, Notch, 2.3 kHz, Q 10.00", band.Description);
        band.Type = MicEq.LowShelf;
        Assert.Equal("lowShelf", sent.Band().Change.Type);
        Assert.True(band.UsesGain);
        Assert.Equal(1.25, band.Gain);
        // The switch, as a double click or Space sends it.
        band.Enabled = false;
        Assert.Equal(new EqBandChange(Enabled: false), sent.Band().Change);

        // The Q field's spinner moves a whole grid step even where 0.01 is finer.
        eq.Bands[1].SetQ(1);
        Assert.Equal(DaemonEq.SnapQ(1), sent.Band().Change.Q);
        decimal shown = eq.Bands[1].QValue!.Value;
        Assert.Equal(1.02m, shown);
        eq.Bands[1].QValue = shown + 0.01m;
        Assert.Equal(DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(1) + 0.016)), sent.Band().Change.Q);
        eq.Bands[1].FrequencyValue = 999.6m;
        Assert.Equal(1000, sent.Band().Change.Frequency);
        eq.Bands[1].GainValue = -40m;
        Assert.Equal(-12, sent.Band().Change.Gain);
    }

    [Fact]
    public void BandChangesGoOutAtMostOnceAnIntervalMergedAndEndingOnTheLast()
    {
        var times = new ConcurrentQueue<(int Band, long At, EqBandChange Change)>();
        var watch = Stopwatch.StartNew();
        using (var sender = new EqBandSender(TimeSpan.FromMilliseconds(20), (band, change) => times.Enqueue((band, watch.ElapsedTicks, change))))
        {
            // Band 2 dragged for about 300 ms with a move every 2 ms, band 3's gain once on the way.
            for (int i = 1; i <= 150; i++)
            {
                sender.Queue(2, new EqBandChange(Frequency: 300 + i));
                if (i % 2 == 0) sender.Queue(2, new EqBandChange(Gain: i * 0.25 % 12));
                if (i == 75) sender.Queue(3, new EqBandChange(Q: 2));
                Thread.Sleep(2);
            }
            Thread.Sleep(80);
        }
        var sent = times.ToArray();
        var two = sent.Where(s => s.Band == 2).ToArray();
        for (int i = 1; i < two.Length; i++)
        {
            double gap = (two[i].At - two[i - 1].At) * 1000.0 / Stopwatch.Frequency;
            Assert.True(gap >= 19.9, $"two changes of band 2 {gap:0.0} ms apart");
        }
        Assert.InRange(two.Length, 5, (int)(watch.ElapsedMilliseconds / 20) + 2);
        // The last change of the drag arrives, with every field that moved.
        Assert.Equal(450, two[^1].Change.Frequency);
        Assert.Equal(150 * 0.25 % 12, two[^1].Change.Gain);
        Assert.Contains(two, s => s.Change.Frequency is not null && s.Change.Gain is not null);
        // Another band is not held back by the first.
        Assert.Single(sent, s => s.Band == 3 && s.Change.Q == 2);
    }

    [Fact]
    public void TheComparisonBypassesWhileTheSwitchStaysOn()
    {
        var (eq, sent) = Model();
        eq.Apply(State(false, Defaults, "null"), null);
        Assert.False(eq.Enabled);
        Assert.False(eq.IsLit);
        Assert.Equal(Localizer.Text("EqStatusOff"), eq.Status);
        // Only an EQ that is on is compared.
        eq.Comparing = true;
        Assert.False(eq.Comparing);
        Assert.Empty(sent.Sent);

        eq.Enabled = true;
        Assert.True(sent.Sent.TryDequeue(out var on));
        Assert.Equal(("setEqEnabled", "xlr1", (object)true), on);
        // A report from before the switch does not take it back.
        eq.Apply(State(false, Defaults, "null"), null);
        Assert.True(eq.Enabled);
        eq.Apply(State(true, Defaults), null);
        Assert.True(eq.IsLit);
        Assert.Equal(Localizer.Format("EqButtonNameOn", "XLR 1"), eq.ButtonName);

        eq.Comparing = true;
        Assert.True(sent.Sent.TryDequeue(out var bypass));
        Assert.Equal(false, bypass.Value);
        eq.Apply(State(false, Defaults, "\"software\""), null);
        Assert.True(eq.Enabled);
        Assert.True(eq.Comparing);
        Assert.False(eq.IsLit);
        Assert.Equal(Localizer.Text("EqStatusComparing"), eq.Status);
        // Closing the editor ends it.
        eq.EndComparison();
        Assert.True(sent.Sent.TryDequeue(out var back));
        Assert.Equal(true, back.Value);
        Assert.False(eq.Comparing);

        // Unavailable: the reason, and the button's alert.
        eq.Apply(JsonNode.Parse($$"""{"enabled":true,"bands":[{{Defaults}}],"backend":null,"available":false,"error":"The native plugin host is not installed."}""")!, null);
        Assert.True(eq.IsFaulty);
        Assert.False(eq.IsLit);
        Assert.Equal("The native plugin host is not installed.", eq.Status);
        Assert.Equal(Localizer.Format("EqButtonNameUnavailable", "XLR 1"), eq.ButtonName);
    }

    [Fact]
    public void CopyPasteAndResetSendOneWholeEq()
    {
        EqClipboard.Value = null;
        var (first, sentFirst) = Model("xlr1");
        var (second, sentSecond) = Model("xlr2");
        Assert.False(second.CanPaste);
        first.Bands[1].DragTo(1000, 6);
        first.Bands[3].Enabled = true;
        first.Enabled = true;
        first.Copy();
        Assert.True(second.CanPaste);
        second.Paste();
        Assert.True(sentSecond.Sent.TryDequeue(out var pasted));
        Assert.Equal("setEq", pasted.Command);
        Assert.Equal("xlr2", pasted.Channel);
        Assert.Equal(first.Current, (MicEq)pasted.Value);
        Assert.Equal(first.Current, second.Current);
        Assert.Equal(1000, second.Bands[1].Frequency);

        second.Reset();
        Assert.True(sentSecond.Sent.TryDequeue(out var reset));
        Assert.Equal(MicEq.Default with { Enabled = true }, (MicEq)reset.Value);
        Assert.Equal([36d, 310, 2172, 15049], second.Bands.Select(b => b.Frequency));
        Assert.True(second.Enabled);
        Assert.Empty(sentSecond.Sent);
        first.Dispose();
        second.Dispose();
        EqClipboard.Value = null;
    }

    private static readonly MethodInfo ApplyMixer =
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void OnlyAChannelTheDaemonReportsAnEqForHasOne()
    {
        var model = new MainViewModel(new DaemonClient());
        string Mixer(string eq) => """
            {"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1EQ},{"id":"game","name":"Game","volume":1}],
             "mixes":[{"id":"monitor","name":"Monitor A"}],
             "eqLimits":{"bands":4,"frequency":{"min":20,"max":20000,"step":1},"gain":{"min":-10,"max":10,"step":0.25},"q":{"min":0.1,"max":10,"log10Step":0.016}}}
            """.Replace("EQ", eq);
        // An older daemon: no EQ anywhere, so no button.
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(""))]);
        using var matrix = new MixMatrixViewModel(model);
        Assert.All(model.Channels, c => Assert.Null(c.Eq));
        Assert.All(matrix.Rows, r => Assert.False(r.ShowsEq));
        // Tall only for the gain over the volume, as before the EQ.
        Assert.Equal(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked, matrix.Rows.Single(r => r.Channel.Id == "xlr1").Tall);

        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(",\"eq\":" + State(true, Defaults).ToJsonString()))]);
        ChannelViewModel xlr1 = model.Channels.Single(c => c.Id == "xlr1");
        MicEqViewModel eq = Assert.IsType<MicEqViewModel>(xlr1.Eq);
        Assert.Null(model.Channels.Single(c => c.Id == "game").Eq);
        Assert.True(eq.Enabled);
        // The daemon's limits, not the model's own.
        Assert.Equal(10, eq.Limits.Gain.Max);
        eq.Bands[1].SetGain(11);
        Assert.Equal(10, eq.Bands[1].Gain);
        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == "xlr1");
        Assert.True(row.ShowsEq);
        Assert.True(row.Tall);
        Assert.False(matrix.Rows.Single(r => r.Channel.Id == "game").ShowsEq);
        // The Flow window's step, first in the input's card.
        string processing = FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:xlr1").Processing;
        Assert.Equal(Localizer.Format("FlowEq", 3), processing.Split('\n')[0]);

        // The daemon stops reporting it: the model goes with it, and so does the button.
        ApplyMixer.Invoke(model, [JsonNode.Parse(Mixer(""))]);
        Assert.Null(xlr1.Eq);
        Assert.False(row.ShowsEq);
        Assert.DoesNotContain(FlowGraph.From(model).Nodes.Single(n => n.Key == "ch:xlr1").Processing.Split('\n'), l => l == Localizer.Format("FlowEq", 3));
    }
}
