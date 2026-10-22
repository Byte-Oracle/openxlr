using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class BrowserSessionSocketTests
{
    [Fact]
    public async Task OnlyTheOwningSocketCanPublishTabsAndDisconnectRemovesThem()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-browser-socket-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", directory);
        try
        {
            ApiToken.Initialize();
            var config = new ConfigurationBuilder().Build();
            using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
            using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices);
            using var lifetime = new Lifetime();
            var hub = new WebSocketHub(devices, service, NullLogger<WebSocketHub>.Instance, lifetime);
            Mixer mixer = (Mixer)typeof(MixerService).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = await SocketTestServer.Start(async (socket, _) =>
            {
                try { await hub.HandleAsync(socket); }
                finally { disconnected.TrySetResult(); }
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var first = new ClientWebSocket();
            using var second = new ClientWebSocket();
            try
            {
                await first.ConnectAsync(new Uri(server.Url), deadline.Token);
                await second.ConnectAsync(new Uri(server.Url), deadline.Token);
                await SocketTestServer.Send(first, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                await SocketTestServer.Send(second, new { cmd = "auth", token = ApiToken.Current }, deadline.Token);
                var one = await Query(first, new { cmd = "getBrowserSession" }, deadline.Token);
                var two = await Query(second, new { cmd = "getBrowserSession" }, deadline.Token);
                string session = one.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>();
                string other = two.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>();
                Assert.NotEqual(session, other);
                Assert.True(BrowserTabRouting.ValidSession(session));
                await Query(first, new { cmd = "syncBrowserTabs", session, tabs = new[] { new { id = 1, site = "https://teams.microsoft.com", title = "Call", capturing = false } } }, deadline.Token);
                await Query(second, new { cmd = "syncBrowserTabs", session = other, tabs = new[] { new { id = 2, site = "https://youtube.com", title = "Video", capturing = true } } }, deadline.Token);
                Assert.Equal(2, mixer.BrowserTabs.Count);
                var denied = await Query(second, new { cmd = "syncBrowserTabs", session, tabs = Array.Empty<object>() }, deadline.Token);
                Assert.Contains("does not own", denied.Last()["error"]!.GetValue<string>());
                Assert.Equal(2, mixer.BrowserTabs.Count);
                var repeated = await Query(first, new { cmd = "getBrowserSession" }, deadline.Token);
                Assert.Equal(session, repeated.Single(m => m["type"]?.GetValue<string>() == "browserSession")["session"]!.GetValue<string>());
                Assert.False((await hub.ExecuteForApiAsync("""{"cmd":"getBrowserSession"}""")).Ok);
                Assert.False((await hub.ExecuteForApiAsync(JsonSerializer.Serialize(new { cmd = "syncBrowserTabs", session, tabs = Array.Empty<object>() }))).Ok);
                await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
                await disconnected.Task.WaitAsync(deadline.Token);
                Assert.Equal(other, Assert.Single(mixer.BrowserTabs).Session);
            }
            finally { lifetime.StopApplication(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<List<JsonNode>> Query(WebSocket socket, object command, CancellationToken stop)
    {
        string request = Guid.NewGuid().ToString("N");
        JsonObject body = JsonSerializer.SerializeToNode(command)!.AsObject();
        body["requestId"] = request;
        await SocketTestServer.Send(socket, body, stop);
        var replies = new List<JsonNode>();
        while (true)
        {
            JsonNode message = await SocketTestServer.Receive(socket, stop);
            replies.Add(message);
            if (message["type"]?.GetValue<string>() == "commandResult" && message["requestId"]?.GetValue<string>() == request) return replies;
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => _stop.Token;
        public void StopApplication() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
