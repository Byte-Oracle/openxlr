using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>
/// Ducking in the Options window's interface card: on or off, the outputs
/// it lowers and its three presets, each sent as a <c>setDucking</c> that
/// carries only the field that changed, with the EQ editor's badge while
/// the unit's own processor runs it and a line saying what runs it or why
/// nothing does. Shown only while the driven unit's capabilities say
/// <c>ducking</c>, the one place it runs today.
/// </summary>
public sealed partial class MainViewModel
{
    // The API's words for each choice, in the order the pickers list them.
    internal static readonly string[] DuckingSensitivities = ["low", "medium", "high"];
    internal static readonly string[] DuckingAmounts = ["low", "medium", "high"];
    internal static readonly string[] DuckingSpeeds = ["slow", "medium", "fast"];

    private bool _capDucking;
    /// <summary>The driven unit ducks its own outputs, keyed by XLR 1.</summary>
    public bool CapDucking { get => _capDucking; private set { if (Set(ref _capDucking, value)) Raise(nameof(ShowDucking)); } }

    public bool ShowDucking => DeviceConnected && CapDucking;

    private bool _duckingEnabled;
    public bool DuckingEnabled
    {
        get => _duckingEnabled;
        set { if (Set(ref _duckingEnabled, value) && !_applying) SendDucking("enabled", value); }
    }

    private bool _duckMonitor;
    /// <summary>The Pro's headphone jacks and Line Out, which the monitor feed reaches through USB pair 2/3.</summary>
    public bool DuckMonitor
    {
        get => _duckMonitor;
        set { if (Set(ref _duckMonitor, value) && !_applying) SendDuckingTargets(); }
    }

    private bool _duckAux;
    /// <summary>The USB Aux port, which the Aux mix reaches through USB pair 10/11.</summary>
    public bool DuckAux
    {
        get => _duckAux;
        set { if (Set(ref _duckAux, value) && !_applying) SendDuckingTargets(); }
    }

    private int _duckingSensitivity = 1;
    public int DuckingSensitivity
    {
        get => _duckingSensitivity;
        set { if (Set(ref _duckingSensitivity, value) && !_applying && value >= 0) SendDucking("sensitivity", DuckingSensitivities[value]); }
    }

    private int _duckingAmount = 1;
    public int DuckingAmount
    {
        get => _duckingAmount;
        set { if (Set(ref _duckingAmount, value) && !_applying && value >= 0) SendDucking("amount", DuckingAmounts[value]); }
    }

    private int _duckingSpeed = 1;
    public int DuckingSpeed
    {
        get => _duckingSpeed;
        set { if (Set(ref _duckingSpeed, value) && !_applying && value >= 0) SendDucking("speed", DuckingSpeeds[value]); }
    }

    private bool _duckingOnDevice;
    /// <summary>The unit's own processor runs the setup: the badge beside the heading, as on the EQ editor.</summary>
    public bool DuckingOnDevice { get => _duckingOnDevice; private set => Set(ref _duckingOnDevice, value); }

    private string _duckingDeviceName = "";
    public string DuckingBadgeTip => Localizer.Format("DuckingHardwareBadgeTip", _duckingDeviceName);

    private string _duckingStatus = "";
    /// <summary>What runs ducking, or why nothing does: the line under the controls.</summary>
    public string DuckingStatus { get => _duckingStatus; private set => Set(ref _duckingStatus, value); }

    private bool _duckingWarns;
    /// <summary>The status says something is wrong: the unit cannot run the setup or did not keep it.</summary>
    public bool DuckingWarns { get => _duckingWarns; private set => Set(ref _duckingWarns, value); }

    private void SendDucking(string field, object value)
        => _ = _client.SetDuckingAsync(new Dictionary<string, object> { [field] = value });

    private void SendDuckingTargets()
    {
        var targets = new List<string>();
        if (_duckMonitor) targets.Add("monitor");
        if (_duckAux) targets.Add("aux");
        SendDucking("targets", targets.ToArray());
    }

    /// <summary>Read the capability and the state's <c>ducking</c>; called with the rest of a state push.</summary>
    private void ApplyDucking(JsonNode? capabilities, JsonNode? ducking)
    {
        CapDucking = capabilities?["ducking"]?.GetValue<bool>() ?? false;
        Raise(nameof(ShowDucking));
        if (ducking is null) return;
        DuckingEnabled = ducking["enabled"]?.GetValue<bool>() ?? false;
        string[] targets = (ducking["targets"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").ToArray() ?? [];
        DuckMonitor = targets.Contains("monitor");
        DuckAux = targets.Contains("aux");
        DuckingSensitivity = System.Array.IndexOf(DuckingSensitivities, ducking["sensitivity"]?.GetValue<string>());
        DuckingAmount = System.Array.IndexOf(DuckingAmounts, ducking["amount"]?.GetValue<string>());
        DuckingSpeed = System.Array.IndexOf(DuckingSpeeds, ducking["speed"]?.GetValue<string>());
        JsonNode? device = ducking["device"];
        DuckingOnDevice = ducking["backend"]?.GetValue<string>() == "device";
        _duckingDeviceName = device?["name"]?.GetValue<string>() ?? "";
        Raise(nameof(DuckingBadgeTip));
        bool ready = device?["ready"]?.GetValue<bool>() ?? false;
        bool held = device?["held"]?.GetValue<bool>() ?? true;
        string? reason = device?["reason"]?.GetValue<string>();
        (DuckingStatus, DuckingWarns) =
            !ready ? (reason == "keyNotSupported" ? Localizer.Format("DuckingKeyNotSupported", _duckingDeviceName)
                      : device?["note"]?.GetValue<string>() ?? Localizer.Text("DuckingNotRunning"), true)
            : !DuckingEnabled ? (Localizer.Text("DuckingStatusOff"), false)
            : !held ? (Localizer.Text("DuckingNotHeld"), true)
            : (Localizer.Format("DuckingStatusDevice", _duckingDeviceName), false);
    }
}
