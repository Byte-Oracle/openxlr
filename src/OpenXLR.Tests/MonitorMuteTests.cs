using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMuteTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("\"true\"", false)]
    public void MonitorMuteRequiresABoolean(string value, bool valid)
    {
        using var mixer = new Mixer();
        Command command = JsonSerializer.Deserialize<Command>("{\"cmd\":\"setMonitorMuted\",\"value\":" + value + "}")!;
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void MonitorMuteRequiresAValue()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setMonitorMuted" }, mixer, _ => null));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void RepeatedTogglesUseTheCurrentStateAndKeepIndividualMixMutes(int presses, bool muted)
    {
        using var mixer = new Mixer();
        mixer.SetMixMuted("chat", true);
        for (int press = 0; press < presses; press++)
        {
            mixer.ToggleMonitorMuted();
            Assert.Equal(press % 2 == 0, mixer.MonitorMuted);
        }
        Assert.Equal(muted, mixer.MonitorMuted);
        Assert.Equal(["chat"], mixer.ExportSettings().MixMuted);
    }

    [Fact]
    public void ConcurrentTogglesRetainEveryTransition()
    {
        using var mixer = new Mixer();
        Parallel.For(0, 101, _ => mixer.ToggleMonitorMuted());
        Assert.True(mixer.MonitorMuted);
        Parallel.For(0, 101, _ => mixer.ToggleMonitorMuted());
        Assert.False(mixer.MonitorMuted);
    }

    [Fact]
    public void AnExplicitToggleCancelsAQueuedResume()
    {
        using var mixer = new Mixer();
        mixer.SetMonitorMuted(true);
        typeof(Mixer).GetField("_desktopMonitorIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, false);

        mixer.ToggleMonitorMuted();

        Assert.True(mixer.MonitorMuted);
        Assert.Null(typeof(Mixer).GetField("_desktopMonitorIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer));
    }

    [Fact]
    public async Task TheNoArgumentToggleIsDispatchedAndAcknowledgedForEveryPress()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-monitor-toggle-").FullName;
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), "printf '[]'\n");
            Environment.SetEnvironmentVariable("PATH", directory);
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            var mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            const string json = """{"cmd":"toggleMonitorMuted","requestId":"monitor-key"}""";
            Assert.Null(CommandValidation.Check(JsonSerializer.Deserialize<Command>(json)!, mixer, _ => null));

            foreach (bool muted in new[] { true, false, true })
            {
                ApiCommandResult result = await hub.ExecuteForApiAsync(json);
                Assert.True(result.Ok);
                CommandResultMessage outcome = Assert.Single(result.Messages.OfType<CommandResultMessage>());
                Assert.Equal("monitor-key", outcome.RequestId);
                Assert.Null(outcome.Error);
                Assert.Equal(muted, Assert.Single(result.Messages.OfType<StateMessage>()).Mixer!.MonitorMuted);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TheMasterMuteIsSavedWithoutReplacingIndividualMixMutes()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-monitor-mute-").FullName;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            Assert.Null(new MixerSettings { MonitorMuted = true, MixMuted = ["monitor2"] }.Save(path));
            MixerSettings restored = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out var warning));
            Assert.Null(warning);
            Assert.True(restored.MonitorMuted);
            Assert.Equal(["monitor2"], restored.MixMuted);
            File.WriteAllText(path, "{}");
            Assert.False(MixerSettings.Load(path)!.MonitorMuted);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("monitor", true)]
    [InlineData("virtualMic", false)]
    [InlineData("auxPort", false)]
    public void TheWindowShowsEffectiveMonitorMuteWithoutChangingTheRawMixIntent(string kind, bool muted)
    {
        var raw = System.Text.Json.Nodes.JsonNode.Parse("{\"kind\":\"" + kind + "\",\"muted\":false}")!;
        var model = new OpenXLR.UI.MixViewModel(new OpenXLR.UI.DaemonClient(), "monitor", "Monitor");

        model.ApplyFromDaemon(raw, monitorMuted: true);

        Assert.Equal(muted, model.Muted);
        Assert.False(raw["muted"]!.GetValue<bool>());
        model.ApplyFromDaemon(raw, monitorMuted: false);
        Assert.False(model.Muted);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"removed\":true,\"OpenXLR_mix_monitor\":false}")]
    public void InvalidNativeRestoreOwnersAreDroppedWithoutDiscardingOtherSettings(string owners)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-native-mute-settings-").FullName;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            File.WriteAllText(path, "{\"monitorMuted\":true,\"monitorOutputs\":[\"external\"],\"monitorOutputMutes\":" + owners + "}");
            var settings = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.True(settings.MonitorMuted);
            Assert.Equal(["external"], settings.MonitorOutputs);
            Assert.Empty(settings.MonitorOutputMutes);
            Assert.NotNull(warning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("@DEFAULT_SINK@")]
    [InlineData("123")]
    [InlineData("-anything")]
    [InlineData("")]
    public void NativeRestoreOwnershipNeverResolvesAnAliasAsADifferentPhysicalDevice(string name)
    {
        var settings = new MixerSettings { MonitorOutputs = [name], MonitorOutputMutes = new() { [name] = false } };
        var sanitized = SavedMixerValidation.Sanitize(settings, out var warnings);
        Assert.Empty(sanitized.MonitorOutputMutes);
        Assert.NotEmpty(warnings);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
