using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;

namespace OpenXLR.Tests;

/// <summary>The full, compact and mini views of the submixer, and where their choices are kept.</summary>
[Collection("xdg-config")]
public sealed class MixerViewTests
{
    [Fact]
    public async Task CompactShowsTheChosenChannelEvenHiddenAndFallsBackWithoutForgettingIt()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music", "chat"], hidden: "game");
            ChannelViewModel game = Channel(model, "game"), music = Channel(model, "music");
            Assert.False(game.DisplayVisible);
            Assert.True(music.DisplayVisible);

            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Assert.True(model.ShowChannelSelector);
            Assert.False(model.ShowMixSelector);
            Assert.True(model.ShowDetailedSections);
            Assert.Same(music, model.SelectedCompactChannel);   // the first channel that is not hidden
            model.SelectedCompactChannel = game;
            Assert.Equal(["game"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
            Assert.All(game.Sends, send => Assert.True(send.DisplayVisible));
            Assert.All(model.Mixes, mix => Assert.True(mix.DisplayVisible));
            Assert.Equal("compact", UiSettings.Load().MixerView);
            Assert.Equal("game", UiSettings.Load().CompactChannel);

            Apply(model, ["music", "chat"], hidden: null);
            Assert.Same(Channel(model, "music"), model.SelectedCompactChannel);
            Assert.Equal("game", UiSettings.Load().CompactChannel);
            Apply(model, ["game", "music", "chat"], hidden: "game");
            Assert.Equal("game", model.SelectedCompactChannel!.Id);
            Assert.Equal(0.42, Channel(model, "game").Sends[0].Level);

            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Assert.False(Channel(model, "game").DisplayVisible);
            Assert.Null(UiSettings.Load().MixerView);
        });
    }

    [Fact]
    public async Task MiniShowsOneSendAndOneMasterAndSetsSectionsAside()
    {
        await InConfig(async () =>
        {
            new UiSettings { MixerView = "mini", CompactChannel = "music", CompactMix = "stream" }.Save();
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music"], hidden: null);
            Assert.True(model.IsMiniView);
            Assert.False(model.ShowDetailedSections);
            Assert.False(model.ShowApplications);
            Assert.Equal(["music"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
            Assert.Equal(["stream"], model.Mixes.Where(m => m.DisplayVisible).Select(m => m.Id));
            Assert.Equal(["stream"], Channel(model, "music").Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));

            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "monitor");
            Assert.Equal(["monitor"], model.Mixes.Where(m => m.DisplayVisible).Select(m => m.Id));
            Assert.Equal("monitor", UiSettings.Load().CompactMix);
            Assert.All(Channel(model, "music").Sends, send => Assert.Equal(0.42, send.Level));
        });
    }

    [Fact]
    public async Task AChoiceThatCannotBeSavedStaysForTheRunAndSaysWhy()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            Apply(model, ["game", "music"], hidden: null);
            var options = new OptionsViewModel(client, model);
            string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
            Directory.CreateDirectory(path);
            try
            {
                options.SelectedMixerView = options.MixerViewChoices.Single(c => c.Id == MainViewModel.CompactView);
                Assert.True(model.IsCompactView);
                Assert.NotNull(options.PreferenceError);
                model.SelectedCompactChannel = Channel(model, "music");
                Assert.Equal(["music"], model.Channels.Where(c => c.DisplayVisible).Select(c => c.Id));
                Assert.NotNull(model.ViewPreferenceError);
            }
            finally { Directory.Delete(path); }
            model.SelectedCompactChannel = Channel(model, "game");
            Assert.Null(model.ViewPreferenceError);
            options.SelectedMixerView = options.MixerViewChoices.Single(c => c.Id == MainViewModel.MiniView);
            Assert.Null(options.PreferenceError);
            Assert.Equal("mini", UiSettings.Load().MixerView);
        });
    }

    [Fact]
    public async Task AHiddenMixLeavesTheFullAndCompactViewsButKeepsItsSendsAndAppearsInTheMiniView()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            ApplyMixes(model, hiddenMixes: ["monitor2"]);
            MixViewModel monitor2 = model.Mixes.Single(m => m.Id == "monitor2");
            Assert.Contains(monitor2, model.Mixes);
            Assert.False(monitor2.DisplayVisible);
            Assert.False(monitor2.Revealed);
            Assert.Equal(["monitor", "stream", "chat"], model.Mixes.Where(m => m.DisplayVisible).Select(m => m.Id));
            foreach (ChannelViewModel channel in model.Channels)
            {
                Assert.Equal(["monitor", "stream", "chat"], channel.Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));
                Assert.Equal(0.42, channel.Sends.Single(s => s.MixId == "monitor2").Level);
            }
            Assert.True(model.CanShowHidden);

            // The matrix drops the column and the cell under it in every row, so the columns still line up.
            using (var matrix = new MixMatrixViewModel(model))
            {
                Assert.Equal(["monitor", "stream", "chat"], matrix.Columns.Select(c => c.Mix.Id));
                Assert.All(matrix.Rows, row => Assert.Equal(matrix.Columns, row.Cells.Select(c => c.Column)));
                Assert.All(matrix.Rows, row => Assert.DoesNotContain(row.Cells, c => c.Mix.Id == "monitor2"));
            }

            // The compact view draws every mix too, so the hidden one stays out.
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Assert.False(monitor2.DisplayVisible);
            Assert.Equal(["monitor", "stream", "chat"], model.SelectedCompactChannel!.Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));
            Assert.True(model.CanShowHidden);

            // The mini view draws the mix chosen for it, hidden or not, and falls back to one that is not hidden.
            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            Assert.False(model.CanShowHidden);
            Assert.Equal("monitor", model.SelectedCompactMix!.Id);
            model.SelectedCompactMix = monitor2;
            Assert.True(monitor2.DisplayVisible);
            Assert.False(monitor2.Revealed);
            Assert.Equal(["monitor2"], model.SelectedCompactChannel!.Sends.Where(s => s.DisplayVisible).Select(s => s.MixId));
            Assert.Contains(monitor2, model.Mixes);   // the mini view's picker lists it
        });
    }

    [Fact]
    public async Task ShowHiddenDrawsHiddenChannelsAndMixesFadedAndIsKeptAsAWindowPreference()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            ApplyMixes(model, hiddenMixes: [], hiddenChannel: null);
            Assert.False(model.CanShowHidden);
            Assert.False(model.ShowHidden);

            ApplyMixes(model, hiddenMixes: ["monitor2"], hiddenChannel: "game");
            Assert.True(model.CanShowHidden);
            ChannelViewModel game = Channel(model, "game"), music = Channel(model, "music");
            MixViewModel monitor2 = model.Mixes.Single(m => m.Id == "monitor2");
            Assert.False(game.DisplayVisible);

            model.ShowHidden = true;
            Assert.True(UiSettings.Load().ShowHiddenItems);
            Assert.True(game.DisplayVisible);
            Assert.True(game.Revealed);
            Assert.True(monitor2.DisplayVisible);
            Assert.True(monitor2.Revealed);
            Assert.False(music.Revealed);
            Assert.False(model.Mixes.Single(m => m.Id == "monitor").Revealed);
            SendViewModel musicToB = music.Sends.Single(s => s.MixId == "monitor2");
            SendViewModel gameToA = game.Sends.Single(s => s.MixId == "monitor");
            SendViewModel gameToB = game.Sends.Single(s => s.MixId == "monitor2");
            Assert.True(musicToB.DisplayVisible && musicToB.Revealed && musicToB.MixRevealed);
            // Inside the hidden channel's own faded strip, a row fades once, not twice.
            Assert.True(gameToA.Revealed && !gameToA.MixRevealed);
            Assert.True(gameToB.Revealed && !gameToB.MixRevealed);
            Assert.False(music.Sends.Single(s => s.MixId == "monitor").Revealed);
            using (var matrix = new MixMatrixViewModel(model))
            {
                Assert.Equal(["monitor", "monitor2", "stream", "chat"], matrix.Columns.Select(c => c.Mix.Id));
                Assert.Equal(["game", "music"], matrix.Rows.Select(r => r.Channel.Id));
                Assert.All(matrix.Rows, row => Assert.Equal(matrix.Columns, row.Cells.Select(c => c.Column)));
            }

            // A window started later keeps the choice; nothing reaches the daemon.
            await using var again = new DaemonClient("ws://127.0.0.1:1/ws");
            var reopened = new MainViewModel(again);
            ApplyMixes(reopened, hiddenMixes: ["monitor2"], hiddenChannel: "game");
            Assert.True(reopened.ShowHidden);
            Assert.True(reopened.Mixes.Single(m => m.Id == "monitor2").Revealed);

            model.ShowHidden = false;
            Assert.False(UiSettings.Load().ShowHiddenItems);
            Assert.False(game.DisplayVisible || game.Revealed || monitor2.DisplayVisible || monitor2.Revealed);
            Assert.False(musicToB.DisplayVisible);

            // Nothing hidden any more: the button goes, the preference stays.
            model.ShowHidden = true;
            ApplyMixes(model, hiddenMixes: [], hiddenChannel: null);
            Assert.False(model.CanShowHidden);
            Assert.True(model.ShowHidden);
            Assert.True(UiSettings.Load().ShowHiddenItems);
            Assert.All(model.Mixes, mix => Assert.False(mix.Revealed));
        });
    }

    [Fact]
    public async Task EveryMixHiddenLeavesTheChannelsWithNoSendsAndAnEmptyMatrixHead()
    {
        await InConfig(async () =>
        {
            await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
            var model = new MainViewModel(client);
            ApplyMixes(model, hiddenMixes: ["monitor", "monitor2", "stream", "chat"]);
            Assert.All(model.Mixes, mix => Assert.False(mix.DisplayVisible));
            Assert.All(model.Channels, channel => Assert.True(channel.DisplayVisible));
            Assert.All(model.Channels.SelectMany(c => c.Sends), send => Assert.False(send.DisplayVisible));
            Assert.True(model.CanShowHidden);
            using var matrix = new MixMatrixViewModel(model);
            Assert.Empty(matrix.Columns);
            Assert.Equal(["game", "music"], matrix.Rows.Select(r => r.Channel.Id));
            Assert.All(matrix.Rows, row => Assert.Empty(row.Cells));
            using var banks = new MixerPresentationViewModel(model);
            Assert.Empty(banks.Banks);

            model.ShowHidden = true;
            Assert.Equal(4, matrix.Columns.Count);
            Assert.All(matrix.Rows, row => Assert.Equal(4, row.Cells.Count));
            Assert.All(matrix.Columns, column => Assert.True(column.Mix.Revealed));
        });
    }

    private static void ApplyMixes(MainViewModel model, string[] hiddenMixes, string? hiddenChannel = null)
    {
        string[] mixes = ["monitor", "monitor2", "stream", "chat"];
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray([.. mixes.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["kind"] = id.StartsWith("monitor", StringComparison.Ordinal) ? "monitor" : "virtualMic",
                ["appearance"] = new JsonObject { ["hidden"] = hiddenMixes.Contains(id) },
            })]),
            ["channels"] = new JsonArray([.. new[] { "game", "music" }.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject([.. mixes.Select(m => KeyValuePair.Create(m, (JsonNode?)0.42))]),
                ["appearance"] = new JsonObject { ["hidden"] = id == hiddenChannel },
            })]),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }

    private static ChannelViewModel Channel(MainViewModel model, string id) => model.Channels.Single(c => c.Id == id);

    private static void Apply(MainViewModel model, string[] channels, string? hidden)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(
                new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor" },
                new JsonObject { ["id"] = "stream", ["name"] = "Stream", ["kind"] = "virtualMic" }),
            ["channels"] = new JsonArray([.. channels.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42, ["stream"] = 0.42 },
                ["appearance"] = new JsonObject { ["hidden"] = id == hidden },
            })]),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }

    private static async Task InConfig(Func<Task> action)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-view-").FullName;
        string? old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory); await action(); }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old); Directory.Delete(directory, true); }
    }
}
