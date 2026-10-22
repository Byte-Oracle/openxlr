using System.Text.Json.Serialization;

namespace OpenXLR.Core.Mixing;

/// <summary>Metadata supplied by the browser companion, never audio or a complete browsing URL.</summary>
public sealed record BrowserTabRegistration
{
    public int Id { get; init; } = -1;
    public string Site { get; init; } = "";
    public string Title { get; init; } = "";
    public bool Capturing { get; init; }
    public string? Error { get; init; }
}

/// <summary>A connected tab and the output choice the browser companion should apply.</summary>
public sealed record BrowserTabState(string Session, int Id, string Site, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ChannelId,
    bool Remember, bool Capturing, string? Error);

public static class BrowserTabRouting
{
    public const int MaxProviders = 8, MaxTabs = 128, MaxSites = 128;
    public const int MaxOrigin = 512, MaxTitle = 160, MaxError = 200;

    public static string? CanonicalSite(string? value)
    {
        if (value is not { Length: > 0 and <= MaxOrigin } || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool ValidSession(string? session)
        => session is { Length: 32 } && Guid.TryParseExact(session, "N", out _);

    public static string? CheckTabs(IReadOnlyList<BrowserTabRegistration>? tabs)
    {
        if (tabs is null || tabs.Count > MaxTabs) return $"syncBrowserTabs: supply at most {MaxTabs} tabs";
        var seen = new HashSet<int>();
        foreach (BrowserTabRegistration? tab in tabs)
        {
            if (tab is null || tab.Id < 0 || !seen.Add(tab.Id)) return "syncBrowserTabs: tab ids must be distinct non-negative integers";
            if (CanonicalSite(tab.Site) is null) return "syncBrowserTabs: site must be an HTTP or HTTPS origin without credentials, path, query or fragment";
            if (tab.Title is null || tab.Title.Length > MaxTitle || tab.Title.Any(char.IsControl)) return $"syncBrowserTabs: title must have at most {MaxTitle} characters and no controls";
            if (tab.Error is { } error && (error.Length > MaxError || error.Any(char.IsControl))) return $"syncBrowserTabs: error must have at most {MaxError} characters and no controls";
        }
        return null;
    }

    internal static bool IsChromium(string? binary)
    {
        if (binary is null) return false;
        string name = Path.GetFileName(binary).ToLowerInvariant();
        return name is "chrome" or "google-chrome" or "google-chrome-stable" or "google-chrome-beta" or "google-chrome-unstable"
            or "google chrome" or "chromium" or "chromium-browser" or "ungoogled-chromium"
            or "brave" or "brave-browser" or "brave-browser-beta" or "brave-browser-nightly"
            or "msedge" or "microsoft-edge" or "microsoft-edge-stable" or "microsoft-edge-beta" or "microsoft-edge-dev" or "microsoft edge"
            or "vivaldi" or "vivaldi-bin" or "opera" or "opera-beta" or "opera-developer";
    }

    internal static Dictionary<string, string> SiteChannels(IReadOnlyDictionary<string, string>? values)
    {
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string site, string? channel) in values ?? new Dictionary<string, string>())
            if (kept.Count < MaxSites && CanonicalSite(site) is { } origin
                && channel is { Length: > 0 and <= 36 } && !channel.Any(char.IsControl)) kept.TryAdd(origin, channel);
        return kept;
    }
}

public sealed partial class Mixer
{
    private sealed record BrowserTab(BrowserTabRegistration Metadata, bool Override = false, string? Channel = null);
    private readonly Dictionary<string, Dictionary<int, BrowserTab>> _browserSessions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _browserSiteChannels = new(StringComparer.Ordinal);
    private bool _preserveBrowserOutputs;

    public bool PreserveBrowserOutputs { get { lock (_gate) return _preserveBrowserOutputs; } }

    public IReadOnlyList<BrowserTabState> BrowserTabs { get { lock (_gate) return BrowserTabsLocked(); } }

    public string OpenBrowserSession(Guid owner)
    {
        lock (_gate)
        {
            string session = owner.ToString("N");
            if (!_browserSessions.ContainsKey(session))
            {
                if (_browserSessions.Count >= BrowserTabRouting.MaxProviders) throw new InvalidOperationException("At most eight browser companions can connect.");
                _browserSessions[session] = [];
            }
            return session;
        }
    }

    public bool RemoveBrowserSession(Guid owner)
    {
        lock (_gate) return _browserSessions.Remove(owner.ToString("N"));
    }

    public void SyncBrowserTabs(Guid owner, string session, IReadOnlyList<BrowserTabRegistration> tabs)
    {
        if (BrowserTabRouting.CheckTabs(tabs) is { } problem) throw new ArgumentException(problem);
        lock (_gate)
        {
            if (session != owner.ToString("N") || !_browserSessions.TryGetValue(session, out Dictionary<int, BrowserTab>? previous))
                throw new InvalidOperationException("syncBrowserTabs: this connection does not own the browser session");
            _browserSessions[session] = tabs.Select(t => t with { Site = BrowserTabRouting.CanonicalSite(t.Site)! })
                .ToDictionary(t => t.Id, t => previous.TryGetValue(t.Id, out BrowserTab? held) && held.Metadata.Site == t.Site
                    ? held with { Metadata = t } : new BrowserTab(t));
        }
    }

    public void AssignBrowserTab(string session, int tabId, string site, string? channel, bool remember, Func<MixerSettings, string?> persist)
    {
        lock (_gate)
        {
            if (!_browserSessions.TryGetValue(session, out Dictionary<int, BrowserTab>? tabs) || !tabs.TryGetValue(tabId, out BrowserTab? tab))
                throw new InvalidOperationException("assignBrowserTab: the browser tab is no longer connected");
            string expectedSite = BrowserTabRouting.CanonicalSite(site)
                ?? throw new ArgumentException("assignBrowserTab: supply the tab's HTTP or HTTPS origin");
            if (tab.Metadata.Site != expectedSite) throw new InvalidOperationException("assignBrowserTab: the tab changed website; select it again");
            if (channel is not null && !HasApplicationChannel(channel)) throw new ArgumentException("assignBrowserTab: select an application channel or browser default");
            if (remember && channel is not null && !_browserSiteChannels.ContainsKey(tab.Metadata.Site)
                && _browserSiteChannels.Count >= BrowserTabRouting.MaxSites) throw new InvalidOperationException("At most 128 browser sites can be remembered.");
            var previous = new Dictionary<string, string>(_browserSiteChannels, StringComparer.Ordinal);
            tabs[tabId] = tab with { Override = !remember, Channel = channel };
            if (!remember) return;
            if (channel is null) _browserSiteChannels.Remove(tab.Metadata.Site);
            else _browserSiteChannels[tab.Metadata.Site] = channel;
            try { PersistLocked(persist); }
            catch { _browserSiteChannels = previous; tabs[tabId] = tab; throw; }
        }
    }

    public void SetPreserveBrowserOutputs(bool enabled, Func<MixerSettings, string?>? persist = null)
    {
        lock (_gate)
        {
            bool previous = _preserveBrowserOutputs;
            _preserveBrowserOutputs = enabled;
            try { if (persist is not null) PersistLocked(persist); }
            catch { _preserveBrowserOutputs = previous; throw; }
            if (previous == enabled) return;
            foreach (int id in _streams.Where(p => BrowserTabRouting.IsChromium(p.Value.Identity)).Select(p => p.Key).ToArray()) _streams.Remove(id);
        }
    }

    private IReadOnlyList<BrowserTabState> BrowserTabsLocked()
        => [.. _browserSessions.OrderBy(s => s.Key, StringComparer.Ordinal).SelectMany(s => s.Value.Values.OrderBy(t => t.Metadata.Id)
            .Select(t => new BrowserTabState(s.Key, t.Metadata.Id, t.Metadata.Site, t.Metadata.Title,
                t.Override ? t.Channel : _browserSiteChannels.GetValueOrDefault(t.Metadata.Site),
                !t.Override && _browserSiteChannels.ContainsKey(t.Metadata.Site), t.Metadata.Capturing, t.Metadata.Error)))];

    private void RestoreBrowserSettingsLocked(MixerSettings settings)
    {
        SetPreserveBrowserOutputs(settings.PreserveBrowserOutputs);
        _browserSiteChannels = BrowserTabRouting.SiteChannels(settings.BrowserSiteChannels)
            .Where(p => !_built || HasApplicationChannel(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private void PruneBrowserChannelsLocked()
    {
        foreach (string site in _browserSiteChannels.Where(p => !HasApplicationChannel(p.Value)).Select(p => p.Key).ToArray()) _browserSiteChannels.Remove(site);
        foreach (Dictionary<int, BrowserTab> tabs in _browserSessions.Values)
            foreach ((int id, BrowserTab tab) in tabs.ToArray())
                if (tab.Channel is not null && !HasApplicationChannel(tab.Channel)) tabs[id] = tab with { Channel = null };
    }
}
