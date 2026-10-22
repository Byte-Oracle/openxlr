using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorOutputSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSnapshotsAndRepliesCannotDropANewerOutputFromTheNextSelection(bool oldReplyFails)
    {
        await using var f = await Fixture.Start();
        f.Report(["a"], ["a", "b", "c", "d"]);
        f.Pick("b", true);
        JsonNode first = await f.Next();
        f.Pick("c", true);
        JsonNode second = await f.Next();
        Assert.Equal(["a", "b", "c"], Devices(second));
        f.Report(["a", "b"], ["a", "b", "c", "d"]);
        Assert.Equal(["a", "b", "c"], f.Selected);
        MonitorOutputItem b = f.Model.MonitorOutputs.Single(output => output.Name == "b");
        f.Report(["a", "b"], ["a", "b", "c", "d", "newly-plugged"]);
        Assert.Same(b, f.Model.MonitorOutputs.Single(output => output.Name == "b"));
        await f.Reply(first, oldReplyFails ? "old failure" : null);
        f.Pick("d", true);
        JsonNode third = await f.Next();
        Assert.Equal(["a", "b", "c", "d"], Devices(third));
        f.SetStatus("current status");
        await f.Reply(second, "also old");
        f.Report(["a", "b", "c", "d"], ["a", "b", "c", "d"]);
        await f.Reply(third);
        await Until(() => !f.Pending);
        Assert.Equal(["a", "b", "c", "d"], f.Selected);
        Assert.Equal("current status", f.Model.Status);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task RemovingThenAddingOutputsPreservesTheNewIntentAndMissingSelectedOutputs()
    {
        await using var f = await Fixture.Start();
        f.Report(["missing", "a"], ["a", "b", "c"]);
        f.Pick("a", false);
        JsonNode first = await f.Next();
        Assert.Equal(["missing"], Devices(first));
        f.Pick("b", true);
        JsonNode second = await f.Next();
        f.Report(["missing"], ["a", "b", "c"]);
        Assert.Equal(["b", "missing"], f.Selected);
        Assert.False(f.Model.MonitorOutputs.Single(o => o.Name == "missing").IsPresent);
        f.Pick("c", true);
        JsonNode third = await f.Next();
        Assert.Equal(["missing", "b", "c"], Devices(third));
        await f.Reply(first);
        await f.Reply(second);
        f.Report(["missing", "b", "c"], ["a", "b", "c"]);
        await f.Reply(third);
        await Until(() => !f.Pending);
        Assert.Equal(["b", "c", "missing"], f.Selected);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task ARejectedLatestSelectionRestoresTheReportedOutputsAndTheirFeedsWithoutEchoing()
    {
        await using var f = await Fixture.Start();
        f.Report(["missing", "a"], ["a", "b"]);
        f.Pick("missing", false);
        JsonNode first = await f.Next();
        f.Report(["missing", "a"], ["a", "b"]);
        f.Pick("b", true);
        JsonNode second = await f.Next();
        await f.Reply(first, "obsolete");
        f.Report(["missing", "a"], ["a", "b"]);
        await f.Reply(second, "disk full");
        await Until(() => !f.Pending && f.Model.Status == "disk full");
        Assert.Equal(["a", "missing"], f.Selected);
        Assert.Equal("monitor2", f.Model.MonitorOutputs.Single(o => o.Name == "missing").Feed!.Id);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task ADisconnectedSelectionCannotReplayOrReplaceTheNewSessionsStatus()
    {
        await using var f = await Fixture.Start();
        f.Report(["a"], ["a", "b", "c"]);
        f.Pick("b", true);
        JsonNode old = await f.Next();
        f.Model.CancelMonitorSelectionEdit();
        f.Report(["a"], ["a", "b", "c"]);
        f.Pick("c", true);
        JsonNode current = await f.Next();
        f.SetStatus("new session");
        await f.Reply(old, "old connection failed");
        f.Report(["a", "c"], ["a", "b", "c"]);
        await f.Reply(current);
        await Until(() => !f.Pending);
        Assert.Equal(["a", "c"], f.Selected);
        Assert.Equal("new session", f.Model.Status);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task ClearingEveryOutputIsAnExplicitSelectionNotAFallback()
    {
        await using var f = await Fixture.Start();
        f.Report(["a", "b"], ["a", "b"]);
        f.Pick("a", false);
        JsonNode first = await f.Next();
        f.Pick("b", false);
        JsonNode empty = await f.Next();
        Assert.Empty(Devices(empty));
        f.Report(["b"], ["a", "b"]);
        Assert.Empty(f.Selected);
        await f.Reply(first);
        f.Report([], ["a", "b"]);
        await f.Reply(empty);
        await Until(() => !f.Pending);
        Assert.Empty(f.Selected);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task DeviceDescriptionChangesUpdateLabelsWithoutReplacingPendingSelections()
    {
        await using var f = await Fixture.Start();
        f.Report(["a"], ["a", "b"], "Speakers");
        MonitorOutputItem original = f.Model.MonitorOutputs.Single(o => o.Name == "a");
        f.Pick("b", true);
        JsonNode pending = await f.Next();
        f.Report(["a"], ["a", "b"], "Renamed speakers");
        Assert.Same(original, f.Model.MonitorOutputs.Single(o => o.Name == "a"));
        Assert.Equal("Renamed speakers", original.Label);
        Assert.Equal(["a", "b"], f.Selected);
        Assert.True(f.Pending);
        f.Report(["a", "b"], ["a", "b"], "Renamed speakers");
        await f.Reply(pending);
        await Until(() => !f.Pending);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task DuplicateDeviceNamesCannotReuseOneOutputRowTwice()
    {
        await using var f = await Fixture.Start();
        f.Report(["a"], ["a"]);
        MonitorOutputItem original = Assert.Single(f.Model.MonitorOutputs);
        f.Report(["a"], ["a", "a"]);
        Assert.Same(original, Assert.Single(f.Model.MonitorOutputs));
        Assert.Single(f.Model.Outputs);
        f.Report(["a"], ["a", "b", "a"]);
        f.Pick("b", true);
        JsonNode pending = await f.Next();
        f.Report(["a"], ["b", "b", "a", "a"]);
        Assert.Equal(2, f.Model.MonitorOutputs.Count);
        Assert.Same(original, f.Model.MonitorOutputs.Single(o => o.Name == "a"));
        Assert.Equal(["a", "b"], f.Selected.Order(StringComparer.Ordinal));
        f.Report(["a", "b"], ["b", "a", "a"]);
        await f.Reply(pending);
        await Until(() => !f.Pending);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFeedEditKeepsItsRowAndItsAnswerWhenTheOutputLeavesOrReturns(bool leaves)
    {
        await using var f = await Fixture.Start();
        f.Report(["a", "b"], leaves ? ["a", "b"] : ["a"]);
        MonitorOutputItem b = f.Model.MonitorOutputs.Single(o => o.Name == "b");
        Assert.Equal(leaves, b.IsPresent);
        b.FeedMixes.Single(m => m.Id == "monitor2").IsSelected = true;
        JsonNode edit = await f.Next();
        Assert.Equal("setMonitorFeed", edit["cmd"]!.GetValue<string>());
        Assert.Equal("monitor+monitor2", edit["mix"]!.GetValue<string>());

        // The device goes or comes back while the daemon has not answered:
        // the same row, now marked as it is, still shows the sum it asked for.
        f.Report(["a", "b"], leaves ? ["a"] : ["a", "b"]);
        Assert.Same(b, f.Model.MonitorOutputs.Single(o => o.Name == "b"));
        Assert.Equal(!leaves, b.IsPresent);
        Assert.Equal("monitor+monitor2", b.Feed!.Id);

        await f.Reply(edit, "feed refused");
        await Until(() => f.Model.Status == "feed refused");
        Assert.Equal("monitor", b.Feed!.Id);
        Assert.True(b.IsSelected);
        Assert.False(f.Commands.TryRead(out _));
    }

    private static string[] Devices(JsonNode command) => [.. command["devices"]!.AsArray().Select(n => n!.GetValue<string>())];

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }

    private sealed class Fixture(SocketTestServer server, DaemonClient client, WebSocket socket,
        ChannelReader<JsonNode> commands) : IAsyncDisposable
    {
        internal MainViewModel Model { get; } = new(client);
        internal ChannelReader<JsonNode> Commands => commands;
        internal string[] Selected => [.. Model.MonitorOutputs.Where(o => o.IsSelected).Select(o => o.Name)];
        internal bool Pending => typeof(MainViewModel).GetField("_pendingMonitorSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Model) is not null;

        internal static async Task<Fixture> Start()
        {
            var connected = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commands = Channel.CreateUnbounded<JsonNode>();
            var server = await SocketTestServer.Start(async (socket, stop) =>
            {
                connected.TrySetResult(socket);
                while (!stop.IsCancellationRequested)
                {
                    JsonNode command = await SocketTestServer.Receive(socket, stop);
                    if (command["cmd"]?.GetValue<string>() is "setMonitorOutputs" or "setMonitorFeed") commands.Writer.TryWrite(command);
                }
            });
            var client = new DaemonClient(server.Url);
            var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += online => { if (online) up.TrySetResult(); };
            var result = new Fixture(server, client, await Connect(), commands.Reader);
            return result;

            async Task<WebSocket> Connect()
            {
                client.Start();
                await up.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        internal void SetStatus(string text) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.Status))!.SetValue(Model, text);
        internal void Pick(string name, bool selected) => Model.MonitorOutputs.Single(o => o.Name == name).IsSelected = selected;
        internal Task<JsonNode> Next() => commands.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        internal Task Reply(JsonNode command, string? error = null) => SocketTestServer.Send(socket,
            new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error }, CancellationToken.None);

        internal void Report(string[] selected, string[] available, string? label = null)
        {
            var devices = new JsonArray([.. available.Select(name => (JsonNode)new JsonObject { ["kind"] = 0, ["name"] = name, ["description"] = name == "a" ? label : null })]);
            var mixer = new JsonObject
            {
                ["mixes"] = new JsonArray(new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor" },
                    new JsonObject { ["id"] = "monitor2", ["name"] = "Monitor B", ["kind"] = "monitor" }),
                ["monitorOutputs"] = new JsonArray([.. selected.Select(name => (JsonNode)JsonValue.Create(name))]),
                ["monitorFeeds"] = new JsonObject { ["missing"] = "monitor2" },
            };
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Model, [devices, mixer]);
        }

        public async ValueTask DisposeAsync() { await client.DisposeAsync(); await server.DisposeAsync(); }
    }
}
