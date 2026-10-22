using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserTabRoutingTests
{
    private static BrowserTabRegistration Tab(int id, string site = "https://teams.microsoft.com", string title = "Call")
        => new() { Id = id, Site = site, Title = title };

    [Fact]
    public void ProvidersOwnTheirSessionsAndDisconnectDropsOnlyTheirTabs()
    {
        using var mixer = new Mixer();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(first);
        Assert.Equal(first.ToString("N"), session);
        Assert.Equal(session, mixer.OpenBrowserSession(first));
        string other = mixer.OpenBrowserSession(second);
        mixer.SyncBrowserTabs(first, session, [Tab(1)]);
        mixer.SyncBrowserTabs(second, other, [Tab(1)]);
        Assert.Throws<InvalidOperationException>(() => mixer.SyncBrowserTabs(second, session, [Tab(2)]));
        Assert.Equal(2, mixer.BrowserTabs.Count);
        Assert.True(mixer.RemoveBrowserSession(first));
        Assert.Equal(other, Assert.Single(mixer.BrowserTabs).Session);
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", false, _ => null));
    }

    [Fact]
    public void TabChoicesSurviveMetadataUpdatesAndOnlyRememberedOriginsEnterSettings()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com", "Video")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => throw new InvalidOperationException("Tab-only choices must not save."));
        mixer.SyncBrowserTabs(owner, session, [Tab(1, title: "New call") with { Capturing = true }, Tab(2, "https://youtube.com", "Video")]);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        MixerSettings? saved = null;
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, settings => { saved = settings; return null; });
        Assert.Equal("voicechat", saved!.BrowserSiteChannels["https://teams.microsoft.com"]);
        string json = JsonSerializer.Serialize(saved);
        Assert.DoesNotContain(session, json);
        Assert.DoesNotContain("New call", json);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(3)]);
        Assert.All(mixer.BrowserTabs, tab => { Assert.Equal("voicechat", tab.ChannelId); Assert.True(tab.Remember); });
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", null, false, _ => null);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.False(mixer.BrowserTabs.Single(t => t.Id == 1).Remember);
        Assert.DoesNotContain("BrowserSiteChannels", JsonSerializer.Serialize(mixer.ExportScene()));
        Assert.DoesNotContain("PreserveBrowserOutputs", JsonSerializer.Serialize(mixer.ExportScene()));
    }

    [Fact]
    public void NavigatingToAnotherOriginDropsOnlyTheTabsTemporaryChoice()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", false, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://youtube.com", "music", true, _ => null);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://TEAMS.microsoft.com:443/", "New call"), Tab(2, "https://youtube.com")]);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://youtube.com", "Video"), Tab(2, "https://youtube.com")]);
        BrowserTabState navigated = mixer.BrowserTabs.Single(t => t.Id == 1);
        Assert.Equal("music", navigated.ChannelId);
        Assert.True(navigated.Remember);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://unassigned.example")]);
        Assert.Null(Assert.Single(mixer.BrowserTabs).ChannelId);
        Assert.False(Assert.Single(mixer.BrowserTabs).Remember);
        Assert.Equal("music", mixer.ExportSettings().BrowserSiteChannels["https://youtube.com"]);
    }

    [Theory]
    [InlineData("game", true)]
    [InlineData(null, true)]
    [InlineData("game", false)]
    public void AnAssignmentQueuedBeforeNavigationCannotChangeTheNewWebsite(string? channel, bool remember)
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", true, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://youtube.com", "voicechat", true, _ => null);
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://youtube.com"), Tab(2, "https://youtube.com")]);
        BrowserTabState[] previousTabs = [.. mixer.BrowserTabs];
        var previousSites = mixer.ExportSettings().BrowserSiteChannels;
        int saves = 0;
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", channel, remember,
            _ => { saves++; return null; }));
        Assert.Equal(0, saves);
        Assert.Equal(previousTabs, mixer.BrowserTabs);
        Assert.Equal(previousSites, mixer.ExportSettings().BrowserSiteChannels);
        mixer.AssignBrowserTab(session, 1, "https://YOUTUBE.com:443/", "game", false, _ => { saves++; return null; });
        Assert.Equal("game", mixer.BrowserTabs.Single(tab => tab.Id == 1).ChannelId);
        Assert.Equal("voicechat", mixer.BrowserTabs.Single(tab => tab.Id == 2).ChannelId);
        Assert.Equal(0, saves);
        Assert.Equal(previousSites, mixer.ExportSettings().BrowserSiteChannels);
    }

    [Fact]
    public void RefusedPreferenceSaveKeepsThePreviousChoice()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1)]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", false, _ => null);
        Assert.Throws<IOException>(() => mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, _ => "disk full"));
        Assert.Equal("music", Assert.Single(mixer.BrowserTabs).ChannelId);
        Assert.False(Assert.Single(mixer.BrowserTabs).Remember);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        Assert.Throws<IOException>(() => mixer.SetPreserveBrowserOutputs(true, _ => "disk full"));
        Assert.False(mixer.PreserveBrowserOutputs);
    }

    [Fact]
    public void ForgettingTheWebsiteClearsItsPreferenceAndPreservesOtherTabOverrides()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2)]);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, _ => null);
        mixer.AssignBrowserTab(session, 2, "https://teams.microsoft.com", "music", false, _ => null);
        Assert.Single(mixer.ExportSettings().BrowserSiteChannels);
        mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", null, true, _ => null);
        Assert.Empty(mixer.ExportSettings().BrowserSiteChannels);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Equal("music", mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
        mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2), Tab(3)]);
        Assert.Null(mixer.BrowserTabs.Single(t => t.Id == 3).ChannelId);
    }

    [Fact]
    public void SavedWebsiteChoicesRestoreWithoutRuntimeTabs()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-browser-settings-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        try
        {
            using var original = new Mixer();
            Guid owner = Guid.NewGuid();
            string session = original.OpenBrowserSession(owner);
            original.SyncBrowserTabs(owner, session, [Tab(1)]);
            original.SetPreserveBrowserOutputs(true);
            original.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "voicechat", true, settings => settings.Save(path));
            using var restored = new Mixer();
            restored.ApplySettings(MixerSettings.Load(path)!);
            Assert.True(restored.PreserveBrowserOutputs);
            Assert.Empty(restored.BrowserTabs);
            Guid next = Guid.NewGuid();
            string fresh = restored.OpenBrowserSession(next);
            restored.SyncBrowserTabs(next, fresh, [Tab(99)]);
            Assert.Equal("voicechat", Assert.Single(restored.BrowserTabs).ChannelId);
            Assert.True(Assert.Single(restored.BrowserTabs).Remember);
            string json = File.ReadAllText(path);
            Assert.DoesNotContain(session, json);
            Assert.DoesNotContain("Call", json);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("""{"cmd":"setPreserveBrowserOutputs","value":"yes"}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"bad","tabId":1,"remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":-1,"remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"channel":"music","remember":true}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://example.com/private","channel":"music","remember":true}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","channel":"ignore","remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","channel":"xlr1","remember":false}""")]
    [InlineData("""{"cmd":"assignBrowserTab","session":"00000000000000000000000000000000","tabId":1,"site":"https://teams.microsoft.com","remember":null}""")]
    [InlineData("""{"cmd":"syncBrowserTabs","session":"00000000000000000000000000000000"}""")]
    [InlineData("""{"cmd":"syncBrowserTabs","session":"00000000000000000000000000000000","tabs":[null]}""")]
    public void MalformedBrowserCommandsAreRefused(string json)
    {
        using var mixer = new Mixer();
        Command command = JsonSerializer.Deserialize<Command>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.NotNull(CommandValidation.Check(command, mixer, _ => null));
    }

    [Theory]
    [InlineData("chromium", true)]
    [InlineData("/opt/google/chrome/chrome", true)]
    [InlineData("Microsoft Edge", true)]
    [InlineData("brave-browser", true)]
    [InlineData("vesktop", false)]
    [InlineData("steamwebhelper", false)]
    [InlineData("chromium-electron", false)]
    [InlineData("firefox", false)]
    public void PreservationAppliesOnlyToRecognizedBrowserIdentities(string binary, bool expected)
        => Assert.Equal(expected, BrowserTabRouting.IsChromium(binary));

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?q=private")]
    [InlineData("https://example.com#private")]
    [InlineData("file:///home/private")]
    [InlineData("https://example.com\n")]
    public void SitesCannotCarryBrowsingPathsOrCredentials(string site)
        => Assert.Null(BrowserTabRouting.CanonicalSite(site));

    // The browser's own serialization is the canonical form, so a tab the
    // companion lists is reported back with the text it sent.
    [Theory]
    [InlineData("http://[::ffff:102:304]", "http://[::ffff:102:304]")]
    [InlineData("http://[::ffff:1.2.3.4]", "http://[::ffff:102:304]")]
    [InlineData("http://[::102:304]", "http://[::102:304]")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080", "http://[::1]:8080")]
    [InlineData("http://[1:0:0:2:0:0:3:4]", "http://[1::2:0:0:3:4]")]
    [InlineData("http://[2001:DB8:0:0:1:0:0:0]", "http://[2001:db8:0:0:1::]")]
    [InlineData("https://EXAMPLE.com:443/", "https://example.com")]
    [InlineData("http://example.com:8080", "http://example.com:8080")]
    public void OriginsAreCanonicalInTheBrowsersForm(string site, string expected)
        => Assert.Equal(expected, BrowserTabRouting.CanonicalSite(site));

    [Theory]
    [InlineData("https://a*b.example")]
    [InlineData("https://a!b.example")]
    [InlineData("https://a$b.example")]
    [InlineData("https://a&b.example")]
    [InlineData("https://a(b).example")]
    public void OriginsWithoutACanonicalFormAreRefusedForTheWholeList(string site)
    {
        Assert.Null(BrowserTabRouting.CanonicalSite(site));
        Assert.NotNull(BrowserTabRouting.CheckTabs([Tab(1), Tab(2, site)]));
    }

    [Fact]
    public void RegistrationsAndSavedSitesHaveIndependentBounds()
    {
        using var mixer = new Mixer();
        Guid owner = Guid.NewGuid();
        string session = mixer.OpenBrowserSession(owner);
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(1)]));
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [Tab(1) with { Title = new string('x', 161) }]));
        Assert.Throws<ArgumentException>(() => mixer.SyncBrowserTabs(owner, session, [.. Enumerable.Range(0, 129).Select(id => Tab(id))]));
        for (int n = 0; n < 128; n++)
        {
            mixer.SyncBrowserTabs(owner, session, [Tab(1, $"https://site{n}.example")]);
            mixer.AssignBrowserTab(session, 1, $"https://site{n}.example", "music", true, _ => null);
        }
        mixer.SyncBrowserTabs(owner, session, [Tab(1, "https://last.example")]);
        Assert.Throws<InvalidOperationException>(() => mixer.AssignBrowserTab(session, 1, "https://last.example", "music", true, _ => null));
        Assert.Equal(128, mixer.ExportSettings().BrowserSiteChannels.Count);
        for (int n = 1; n < 8; n++) mixer.OpenBrowserSession(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => mixer.OpenBrowserSession(Guid.NewGuid()));
    }

    [Fact]
    public void DeletingAChannelClearsItsTabAndSavedSiteWithoutChangingOtherTabs()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        Guid owner = Guid.NewGuid();
        string session = graph.Mixer.OpenBrowserSession(owner);
        graph.Mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        graph.Mixer.AssignBrowserTab(session, 1, "https://teams.microsoft.com", "music", true, _ => null);
        graph.Mixer.AssignBrowserTab(session, 2, "https://youtube.com", "system", false, _ => null);
        Assert.Throws<IOException>(() => graph.Mixer.DeleteApplicationChannel("music", _ => "disk full"));
        Assert.Equal("music", graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        MixerSettings? saved = null;
        graph.Mixer.DeleteApplicationChannel("music", settings => { saved = settings; return null; });
        Assert.Empty(saved!.BrowserSiteChannels);
        Assert.Null(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.Equal("system", graph.Mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
    }

    [Fact]
    public void BuildingAfterRestoringSettingsDropsUnknownWebsiteChannels()
    {
        using var graph = new BrowserGraph();
        graph.Mixer.ApplySettings(new MixerSettings
        {
            BrowserSiteChannels = new() { ["https://teams.microsoft.com"] = "deleted", ["https://youtube.com"] = "system" },
        });
        Guid owner = Guid.NewGuid();
        string session = graph.Mixer.OpenBrowserSession(owner);
        graph.Mixer.SyncBrowserTabs(owner, session, [Tab(1), Tab(2, "https://youtube.com")]);
        graph.Mixer.Build(new MixerConfig { Channels = [new("system", "System")], Mixes = [] });
        Assert.Null(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).ChannelId);
        Assert.False(graph.Mixer.BrowserTabs.Single(t => t.Id == 1).Remember);
        Assert.Equal("system", graph.Mixer.BrowserTabs.Single(t => t.Id == 2).ChannelId);
        Assert.Single(graph.Mixer.ExportSettings().BrowserSiteChannels);
    }

    [Fact]
    public void PreservingBrowserOutputsDoesNotMoveOrUnmuteTheirStreams()
    {
        using var graph = new BrowserGraph();
        graph.Mixer.ApplySettings(JsonSerializer.Deserialize<MixerSettings>("""{"PreserveBrowserOutputs":true}""")!);
        graph.Build();
        graph.Mixer.SyncStreams();
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitModeTransitionsSuspendAssignmentsAndReapplyThemAfterwards()
    {
        using var graph = new BrowserGraph();
        graph.Build();
        graph.Mixer.SyncStreams();
        graph.ClearCalls();
        graph.Mixer.SetPreserveBrowserOutputs(true);
        graph.Mixer.SyncStreams();
        graph.Mixer.AssignApp("chromium", "music");
        graph.Mixer.AssignStream(10, "music");
        Assert.DoesNotContain(graph.Calls, c => c.StartsWith("move-sink-input", StringComparison.Ordinal)
            || c.StartsWith("set-sink-input-mute", StringComparison.Ordinal));
        graph.ClearCalls();
        graph.Mixer.SetPreserveBrowserOutputs(false);
        graph.Mixer.SyncStreams();
        Assert.Contains("move-sink-input 100 OpenXLR_ch_music", graph.Calls);
        Assert.Contains("set-sink-input-mute 100 0", graph.Calls);
    }

    private sealed class BrowserGraph : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-browser-routing-").FullName;
        private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; } = new();
        public string[] Calls => File.ReadAllLines(Path.Combine(_directory, "calls"));

        public BrowserGraph()
        {
            File.WriteAllText(Path.Combine(_directory, "calls"), "");
            File.WriteAllText(Path.Combine(_directory, "inputs"), "100\t2\n");
            ExecutableScript.Write(Path.Combine(_directory, "pw-dump"), """
                printf '[{"id":10,"type":"PipeWire:Interface:Node","info":{"props":{"object.serial":100,"media.class":"Stream/Output/Audio","application.process.binary":"chromium","application.name":"Chromium"}}}]'
                """);
            ExecutableScript.Write(Path.Combine(_directory, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/calls"
                case "$*" in
                  'list sinks short') printf '1\tOpenXLR_ch_system\n2\tOpenXLR_ch_music\n' ;;
                  'list sink-inputs short') /bin/cat "$dir/inputs" ;;
                  load-module\ *) printf '1\n' ;;
                  'move-sink-input 100 OpenXLR_ch_system') printf '100\t1\n' > "$dir/inputs" ;;
                  'move-sink-input 100 OpenXLR_ch_music') printf '100\t2\n' > "$dir/inputs" ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", _directory);
        }

        public void Build()
        {
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [new("system", "System"), new("music", "Music")], Mixes = [] });
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
        }

        public void ClearCalls() => File.WriteAllText(Path.Combine(_directory, "calls"), "");

        public void Dispose()
        {
            Mixer.Dispose();
            Environment.SetEnvironmentVariable("PATH", _oldPath);
            Directory.Delete(_directory, true);
        }
    }
}
