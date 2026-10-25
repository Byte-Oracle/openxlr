using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>Bounded JSON lines for clients whose toolkit cannot open a Unix WebSocket.</summary>
internal static class ControlStdio
{
    internal static async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancel = default)
    {
        try
        {
            using var link = new ControlConnection();
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await link.ConnectAsync(connect.Token);
            }
            await WriteAsync(output, "{\"type\":\"controlReady\"}"u8.ToArray(), cancel);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            Task outgoing = SendAsync(input, link.Socket, lifetime.Token);
            Task incoming = ReceiveAsync(link.Socket, output, lifetime.Token);
            try
            {
                Task finished = await Task.WhenAny(outgoing, incoming);
                await finished;
                // EOF closes the sending half, then drains replies through
                // the peer's close handshake. A one-shot pipe keeps its reply.
                if (finished == outgoing) await incoming.WaitAsync(TimeSpan.FromSeconds(5), cancel);
            }
            finally
            {
                lifetime.Cancel();
                link.Socket.Abort();
                try { await Task.WhenAll(outgoing, incoming); } catch (Exception) { /* both tasks observed */ }
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            Console.Error.WriteLine("Control connection: " + ex.Message);
            return 1;
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, Stream output, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var (outcome, message) = await SocketGuard.ReceiveMessageAsync(socket, buffer, 8 * 1024 * 1024,
                SocketGuard.MessageDeadline, cancel);
            if (outcome != SocketGuard.Outcome.Message) return;
            await WriteAsync(output, message!, cancel);
        }
    }

    private static async Task WriteAsync(Stream output, byte[] message, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await output.WriteAsync(message, deadline.Token);
        await output.WriteAsync("\n"u8.ToArray(), deadline.Token);
        await output.FlushAsync(deadline.Token);
    }

    private static async Task SendAsync(Stream input, WebSocket socket, CancellationToken cancel)
    {
        var reader = PipeReader.Create(input, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync(cancel);
                ReadOnlySequence<byte> buffer = read.Buffer;
                if (buffer.PositionOf((byte)'\n') is { } end)
                {
                    var line = buffer.Slice(0, end);
                    if (line.Length > ApiEndpoints.MaxCommandBytes) throw new IOException("Control command exceeds 64 KiB.");
                    byte[] message = line.ToArray();
                    reader.AdvanceTo(buffer.GetPosition(1, end));
                    if (message.Length > 0)
                        await socket.SendAsync(message, WebSocketMessageType.Text, true, cancel);
                }
                else
                {
                    if (buffer.Length > ApiEndpoints.MaxCommandBytes) throw new IOException("Control command exceeds 64 KiB.");
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (read.IsCompleted)
                    {
                        if (buffer.Length != 0) throw new IOException("Control command has no terminating newline.");
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        deadline.CancelAfter(SocketGuard.CloseDeadline);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
                        return;
                    }
                }
            }
        }
        finally { await reader.CompleteAsync(); }
    }
}
