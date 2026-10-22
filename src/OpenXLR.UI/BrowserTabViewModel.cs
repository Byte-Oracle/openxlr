using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>A browser tab reported by the extension, with routing confirmed by the daemon.</summary>
public sealed class BrowserTabViewModel : ViewModelBase
{
    /// <summary>The picker entry that leaves this tab on the browser's normal destination.</summary>
    public const string BrowserDefault = "";

    private sealed record Route(string? Channel, bool Remember);
    private readonly Func<string, int, string, string?, bool, Task<string?>> _assign;
    private readonly Action<string> _reportError;
    private Route _reported = new(null, false);
    private Route? _pending;
    private long _editVersion;
    private bool _applying, _canEdit, _isPending, _capturing;
    private string _title = "", _site = "";
    private string? _error;

    public BrowserTabViewModel(string session, int id,
        Func<string, int, string, string?, bool, Task<string?>> assign, Action<string> reportError)
    {
        Session = session; Id = id; _assign = assign; _reportError = reportError;
    }

    public string Session { get; }
    public int Id { get; }
    public string Title => _title;
    public string Site => _site;
    public ObservableCollection<ChannelChoice> Channels { get; } = [];
    public bool CanEdit => _canEdit;
    public bool CanRemember => CanEdit && ChannelId is not null;
    public bool IsPending => _isPending;
    public bool Capturing => _capturing;
    public string? ChannelId => (_pending ?? _reported).Channel;
    public ChannelChoice? SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Id == (ChannelId ?? BrowserDefault));
        set
        {
            if (_applying || !CanEdit || value is null || !Channels.Contains(value)) return;
            string? channel = value.Id == BrowserDefault ? null : value.Id;
            if (channel == ChannelId) return;
            Submit(new(channel, channel is not null && Remember));
        }
    }

    public bool Remember
    {
        get => (_pending ?? _reported).Remember;
        set
        {
            if (_applying || !CanRemember || value == Remember) return;
            Submit(new(ChannelId, value));
        }
    }

    /// <summary>Remove the website's saved choice and return this tab to the browser default.</summary>
    public void ForgetWebsite()
    {
        if (CanEdit) Submit(new(null, false), forgetWebsite: true);
    }

    /// <summary>Capture is always extension state. Assigning a channel cannot activate capture.</summary>
    public string StatusText => !CanEdit ? Localizer.Text("BrowserTabRoutingDisabled")
        : _error is { Length: > 0 } ? _error
        : IsPending ? Localizer.Text("BrowserTabApplying")
        : Capturing ? Localizer.Text("BrowserTabCapturing")
        : ChannelId is not null ? Localizer.Text("BrowserTabActivationRequired")
        : Localizer.Text("BrowserTabUsesDefault");

    /// <summary>Update the same row without echoing state or closing an unchanged channel picker.</summary>
    public void ApplyFromDaemon(string title, string site, string? channel, bool remember, bool capturing,
        string? error, IReadOnlyList<ChannelChoice> channels, bool canEdit)
    {
        _applying = true;
        try
        {
            bool navigated = _site.Length > 0 && _site != site;
            Set(ref _title, title, nameof(Title));
            Set(ref _site, site, nameof(Site));
            _reported = new(channel, channel is not null && remember);
            if (navigated) CancelPending();
            if (_pending?.Channel is { } target && !channels.Any(c => c.Id == target)) CancelPending();
            if (!Channels.SequenceEqual(channels))
            {
                Channels.Clear();
                foreach (ChannelChoice choice in channels) Channels.Add(choice);
            }
            Set(ref _capturing, capturing, nameof(Capturing));
            _error = error;
            SetCanEdit(canEdit);
            RaiseRoute();
        }
        finally { _applying = false; }
    }

    internal void SetCanEdit(bool value)
    {
        if (!value) CancelPending();
        Set(ref _canEdit, value, nameof(CanEdit));
        Raise(nameof(CanRemember));
        Raise(nameof(StatusText));
    }

    /// <summary>Invalidate replies from a removed tab or a lost connection.</summary>
    internal void CancelPending()
    {
        _editVersion++;
        _pending = null;
        Set(ref _isPending, false, nameof(IsPending));
        RaiseRoute();
    }

    private void Submit(Route route, bool forgetWebsite = false)
    {
        string site = Site;
        long version = ++_editVersion;
        _pending = route;
        _error = null;
        Set(ref _isPending, true, nameof(IsPending));
        RaiseRoute();
        _ = SubmitAsync(site, route, version, forgetWebsite);
    }

    private async Task SubmitAsync(string site, Route route, long version, bool forgetWebsite)
    {
        string? error = await _assign(Session, Id, site, route.Channel, forgetWebsite || route.Remember);
        if (version != _editVersion) return;
        Set(ref _isPending, false, nameof(IsPending));
        // The daemon publishes state before its acknowledgement. A newer
        // client's state can already be here too, so the latest report wins.
        _pending = null;
        if (error is not null)
        {
            _error = error;
            _reportError(error);
        }
        RaiseRoute();
    }

    private void RaiseRoute()
    {
        Raise(nameof(ChannelId)); Raise(nameof(SelectedChannel)); Raise(nameof(Remember));
        Raise(nameof(CanRemember)); Raise(nameof(StatusText));
    }
}
