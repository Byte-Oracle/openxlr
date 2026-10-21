using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.Tui;
using App = OpenXLR.Tui.App;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// Channel and mix appearance: what the daemon accepts and saves, that it
/// never touches routing, how the window and the terminal mixer draw it, and
/// the fallback channel that no longer depends on the layout order.
/// </summary>
[Collection("xdg-config")]
public sealed class LayoutAppearanceTests
{
    [Theory]
    [InlineData("<svg/>", "#123456", false)]
    [InlineData("music", null, true)]
    [InlineData("none", null, true)]
    [InlineData("a-skin-icon2", null, true)]
    [InlineData("Music", null, false)]
    [InlineData("2music", null, false)]
    [InlineData("my_icon", null, false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg", null, false)]
    [InlineData("♪", "red", false)]
    [InlineData("♪", "#fff", false)]
    [InlineData("♪", "#ffffff\"", false)]
    [InlineData("♪", "#12345G", false)]
    [InlineData("♪", "#12abEF", true)]
    [InlineData("", null, true)]
    public void OnlyListedIconsAndHexColoursAreAccepted(string icon, string? colour, bool valid)
    {
        using var mixer = new Mixer();
        var value = new LayoutAppearance(icon, colour);
        Assert.Equal(valid, LayoutAppearance.IsValid(value));
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "game", Appearance = value };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void TheCommandNamesExactlyOneKnownChannelOrMixAndHidesChannelsAndMixes()
    {
        using var mixer = new Mixer();
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "xlr1", Appearance = new("●", null, true) };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        Assert.Null(CommandValidation.Check(command with { Channel = null, Mix = "monitor", Appearance = new("●") }, mixer, _ => null));
        // Every mix can be hidden, the ones that cannot be deleted included; a headphone output cannot.
        foreach (string mix in new[] { "monitor", "monitor2", "stream", "chat", "auxout" })
            Assert.Null(CommandValidation.Check(command with { Channel = null, Mix = mix }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = null, Output = "phones1" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = null, Mix = "gone" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Mix = "monitor" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = null }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = "gone" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Appearance = null }, mixer, _ => null));
    }

    [Fact]
    public void TheWireFormatReadsIntoTheCommand()
    {
        var command = JsonSerializer.Deserialize<Command>(
            """{"cmd":"setLayoutAppearance","channel":"music","appearance":{"icon":"♫","colour":"#12ABCD","hidden":true}}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(new LayoutAppearance("♫", "#12ABCD", true), command.Appearance);
        var partial = JsonSerializer.Deserialize<Command>("""{"cmd":"setLayoutAppearance","mix":"stream","appearance":{"icon":"◆"}}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(new LayoutAppearance("◆"), partial.Appearance);
    }

    [Fact]
    public void AppearanceIsSavedTransactionallyAndLeavesTheLayoutAlone()
    {
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        try
        {
            object config = GetField(mixer, "_config");
            string before = JsonSerializer.Serialize(mixer.ExportSettings());
            Assert.Throws<IOException>(() => mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => "disk full"));
            Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));

            MixerSettings? saved = null;
            mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), settings => { saved = settings; return null; });
            mixer.SetLayoutAppearance("mix:stream", new("◆", "#ABC123"), _ => null);
            Assert.Equal(new LayoutAppearance("♫", "#12ABCD", true), saved!.Appearance["channel:game"]);
            Assert.Same(config, GetField(mixer, "_config"));

            MixerState state = mixer.Snapshot();
            Assert.Equal(MixerConfig.Default().Channels.Select(c => c.Id), state.Channels.Select(c => c.Id));
            Assert.True(state.Channels.Single(c => c.Id == "game").Appearance.Hidden);
            Assert.Equal("◆", state.Mixes.Single(m => m.Id == "stream").Appearance.Icon);
            Assert.Equal(new LayoutAppearanceState("", null, false, "music"), state.Channels.Single(c => c.Id == "music").Appearance);

            // A hidden mix is saved like a hidden channel, and nothing about the mix itself changes.
            MixStatus before2 = state.Mixes.Single(m => m.Id == "monitor2");
            mixer.SetLayoutAppearance("mix:monitor2", new(Hidden: true), settings => { saved = settings; return null; });
            Assert.Equal(new LayoutAppearance(Hidden: true), saved!.Appearance["mix:monitor2"]);
            Assert.Same(config, GetField(mixer, "_config"));
            MixStatus after2 = mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2");
            Assert.True(after2.Appearance.Hidden);
            Assert.Equal(before2 with { Appearance = after2.Appearance }, after2);
            Assert.Equal(MixerConfig.Default().Mixes.Select(m => m.Id), mixer.Snapshot().Mixes.Select(m => m.Id));
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("output:phones1", new(Hidden: true), _ => null));
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("channel:gone", new("●"), _ => null));
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("mix:gone", new(Hidden: true), _ => null));

            // Back to the default leaves nothing behind in the file.
            mixer.SetLayoutAppearance("mix:stream", LayoutAppearance.Default, _ => null);
            Assert.False(mixer.ExportSettings().Appearance.ContainsKey("mix:stream"));
        }
        finally { SetField(mixer, "_built", false); }
    }

    [Fact]
    public void AHandEditedEntryIsDroppedWithoutLosingTheRest()
    {
        var saved = new MixerSettings
        {
            Appearance = new()
            {
                ["channel:music"] = new("♫", "#123456"),
                ["channel:game"] = new("<image/>"),
                ["mix:monitor2"] = new(Hidden: true),
                ["mix:chat"] = null!,
                ["output:phones1"] = new(Hidden: true),
                ["stream"] = new("●"),
            },
        };
        MixerSettings sanitized = SavedMixerValidation.Sanitize(saved, out IReadOnlyList<string> dropped);
        Assert.Equal(["channel:music", "mix:monitor2"], sanitized.Appearance.Keys.Order(StringComparer.Ordinal));
        Assert.True(sanitized.Appearance["mix:monitor2"].Hidden);
        Assert.Contains(dropped, note => note.StartsWith("appearance:", StringComparison.Ordinal));
    }

    [Fact]
    public void AHiddenMixIsRestoredFromTheFileAndItsEntryGoesWithTheMix()
    {
        using var mixer = new Mixer();
        SetField(mixer, "_config", new MixerConfig
        {
            Channels = [new("game", "Game")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
        });
        SetField(mixer, "_built", true);
        try
        {
            // What a restart reads back from mixer.json, without touching an audio graph.
            typeof(Mixer).GetMethod("RestoreAppearanceLocked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mixer,
                [new Dictionary<string, LayoutAppearance> { ["mix:chat"] = new(Hidden: true), ["mix:monitor"] = new(Hidden: true) }]);
            Assert.True(mixer.ExportSettings().Appearance["mix:chat"].Hidden);
            Assert.True(mixer.ExportSettings().Appearance["mix:monitor"].Hidden);

            // The deletion's own save no longer carries the mix's entry; a
            // failed save puts the mix and its entry back.
            MixerSettings? written = null;
            Assert.Throws<IOException>(() => mixer.DeleteMix("chat", settings => { written = settings; return "disk full"; }));
            Assert.False(written!.Appearance.ContainsKey("mix:chat"));
            Assert.True(written.Appearance["mix:monitor"].Hidden);
            Assert.True(mixer.ExportSettings().Appearance["mix:chat"].Hidden);
        }
        finally { SetField(mixer, "_built", false); }
    }

    [Fact]
    public void TheFallbackChannelIsSystemOrTheFirstByIdWhateverTheOrder()
    {
        var config = MixerConfig.Default();
        string[] apps = [.. config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)];
        Assert.Equal("system", config.FallbackApplicationChannel!.Id);
        Assert.Equal("system", config.WithOrder([.. apps.Reverse()], ["monitor", "monitor2", "stream", "chat"]).ResolveApplicationChannel("gone"));

        var withoutSystem = config.WithoutChannel("system");
        string[] rest = [.. apps.Where(id => id != "system")];
        Assert.Equal("browser", withoutSystem.ResolveApplicationChannel("gone"));
        Assert.Equal("browser", withoutSystem.WithOrder([.. rest.Reverse()], ["monitor", "monitor2", "stream", "chat"]).ResolveApplicationChannel("gone"));
        Assert.Equal("browser", withoutSystem.WithOrder(["sfx", .. rest.Where(id => id != "sfx")], ["monitor", "monitor2", "stream", "chat"])
            .FallbackApplicationChannel!.Id);
        Assert.Equal("ignore", withoutSystem.ResolveApplicationChannel("ignore"));
        Assert.Equal("browser", withoutSystem.ResolveApplicationChannel("xlr1"));
    }

    [Fact]
    public async Task TheWindowLeavesOutAHiddenStripButKeepsItsChannel()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, new JsonObject { ["icon"] = "♫", ["colour"] = "#123456", ["hidden"] = true });
        ChannelViewModel game = model.Channels.Single(c => c.Id == "game");
        ChannelViewModel music = model.Channels.Single(c => c.Id == "music");
        Assert.False(game.DisplayVisible);
        Assert.True(game.Visible);
        Assert.True(music.DisplayVisible);
        Assert.Equal("♫", game.Appearance.Icon);
        Assert.True(game.Appearance.HasColour);
        Assert.Equal(0.42, Assert.Single(game.Sends).Level);
        Assert.Contains(game, model.Channels);

        Apply(model, null);
        Assert.True(game.DisplayVisible);
        Assert.False(game.Appearance.HasColour);
        Assert.Equal("", game.Appearance.Icon);
    }

    [Fact]
    public void TheTerminalDrawsTheIconAndColourAndSkipsAHiddenStrip()
    {
        DaemonLink link = new();
        link.Receive(TuiState("""{"icon":"♫","colour":"#12ABCD"}""", """{"hidden":true}"""));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        Screen screen = new(140, 36);
        app.Draw(screen);

        MixerSnapshot mixer = link.State!.Mixer;
        Assert.Equal(["music", "game", "sfx"], mixer.Shown.Select(c => c.Id));
        Assert.Equal(["music", "sfx"], mixer.Strips.Select(c => c.Id));
        (int x, int y) = Find(screen, "♫ Music");
        Assert.Equal(new Rgb(0x12, 0xAB, 0xCD), screen.At(x + 2, y).Fore);
        Assert.Equal((-1, -1), Find(screen, "Game"));
    }

    [Fact]
    public void ControlArrowsMoveAStripPastAHiddenNeighbour()
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        link.Receive(TuiState("{}", """{"hidden":true}"""));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        app.Draw(new Screen(140, 36));

        app.Handle(new KeyPress(Key.Down));                  // Music, the first strip
        app.Handle(new KeyPress(Key.Right, Ctrl: true));
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setLayoutOrder", command.GetProperty("cmd").GetString());
        // Music passes the hidden Game and the strip beside it on screen.
        Assert.Equal(["game", "sfx", "music"], command.GetProperty("channels").EnumerateArray().Select(e => e.GetString()));

        sent.Clear();
        app.Handle(new KeyPress(Key.Up));
        app.Handle(new KeyPress(Key.Up));                    // back to the masters row, no channel
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Left, Ctrl: true));      // the first strip has nowhere to go
        Assert.Empty(sent);
    }

    [Fact]
    public void TheTerminalLeavesAHiddenMixOutOfTheMixerTabAndMovesAMixPastIt()
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        link.Receive(TuiMixState("""{"hidden":true}""", "{}"));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        Screen screen = new(160, 36);
        app.Draw(screen);

        MixerSnapshot mixer = link.State!.Mixer;
        Assert.Equal(["monitor", "podcast", "rec", "chat"], mixer.Mixes.Select(m => m.Id));
        Assert.Equal(["monitor", "podcast", "chat"], mixer.Masters.Select(m => m.Id));
        Assert.Equal((-1, -1), Find(screen, "Recording"));
        Assert.NotEqual((-1, -1), Find(screen, "Podcast"));
        // The matrix tab still lists the hidden mix.
        Assert.Contains(mixer.Mixes, m => m.Id == "rec" && m.Appearance.Hidden);

        // On the masters row: Monitor A, Podcast, Chat. Ctrl+Left moves Chat
        // past the hidden Recording and before Podcast, so it moves on screen.
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Left, Ctrl: true));
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setLayoutOrder", command.GetProperty("cmd").GetString());
        Assert.Equal(["chat", "podcast", "rec"], command.GetProperty("mixes").EnumerateArray().Select(e => e.GetString()));

        // The selection followed to the second master, Podcast in this snapshot,
        // which has only Monitor A before it, and Monitor A keeps its place.
        sent.Clear();
        app.Handle(new KeyPress(Key.Left, Ctrl: true));
        Assert.Empty(sent);
    }

    [Fact]
    public void TheTerminalSaysSoWhenEveryMixIsHidden()
    {
        DaemonLink link = new();
        link.Receive(TuiMixState("""{"hidden":true}""", """{"hidden":true}""", everyMix: true));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        Screen screen = new(160, 36);
        app.Draw(screen);
        Assert.Empty(link.State!.Mixer.Masters);
        Assert.NotEqual((-1, -1), Find(screen, "Every mix is hidden"));
    }

    private static string TuiMixState(string rec, string podcast, bool everyMix = false)
    {
        string other = everyMix ? """{"hidden":true}""" : "{}";
        return $$"""
            {
              "type": "state", "connected": true,
              "mixer": {
                "mixes": [
                  { "id": "monitor", "name": "Monitor A", "volume": 1, "kind": "monitor", "editable": false, "appearance": {{other}} },
                  { "id": "podcast", "name": "Podcast", "volume": 1, "kind": "virtualMic", "editable": true, "appearance": {{podcast}} },
                  { "id": "rec", "name": "Recording", "volume": 1, "kind": "virtualMic", "editable": true, "appearance": {{rec}} },
                  { "id": "chat", "name": "Chat", "volume": 1, "kind": "virtualMic", "editable": true, "appearance": {{other}} }
                ],
                "channels": [
                  { "id": "music", "name": "Music", "levels": { "monitor": 1, "rec": 1, "podcast": 1, "chat": 1 }, "mutedIn": [] }
                ]
              }
            }
            """;
    }

    private static string TuiState(string music, string game) => $$"""
        {
          "type": "state", "connected": true,
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "kind": "monitor" } ],
            "channels": [
              { "id": "music", "name": "Music", "levels": { "monitor": 1 }, "mutedIn": [], "appearance": {{music}} },
              { "id": "game", "name": "Game", "levels": { "monitor": 1 }, "mutedIn": [], "appearance": {{game}} },
              { "id": "sfx", "name": "SFX", "levels": { "monitor": 1 }, "mutedIn": [] }
            ]
          }
        }
        """;

    private static (int X, int Y) Find(Screen screen, string text)
    {
        for (int y = 0; y < screen.Height; y++)
        {
            string row = new([.. Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch)]);
            int x = row.IndexOf(text, StringComparison.Ordinal);
            if (x >= 0) return (x, y);
        }
        return (-1, -1);
    }

    private static void Apply(MainViewModel model, JsonNode? gameAppearance)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A" }),
            ["channels"] = new JsonArray([.. new[] { "game", "music" }.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42 },
                ["appearance"] = id == "game" ? gameAppearance?.DeepClone() : null,
            })]),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }

    private static object GetField(Mixer mixer, string name) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;

    private static void SetField(Mixer mixer, string name, object value) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
