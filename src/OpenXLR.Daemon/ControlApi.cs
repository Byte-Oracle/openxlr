using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>The transport supplies identity before Kestrel reads a request.</summary>
internal static class ControlApi
{
    private sealed record Peer(uint User);

    internal static void Configure(WebApplicationBuilder builder, ControlSocketListener listener)
    {
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.UseSockets(options => options.CreateBoundListenSocket = endpoint =>
            endpoint.Equals(listener.Socket.LocalEndPoint) ? listener.Socket
                : throw new IOException("Only the private control socket can serve the API."));
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Environment variables and appsettings must not add a TCP listener.
            options.Configure(new ConfigurationBuilder().Build());
            options.Limits.MaxConcurrentUpgradedConnections = 32;
            options.Limits.MaxConcurrentConnections = 64;
            options.ListenUnixSocket(listener.Path, listen => listen.Use(next => async connection =>
            {
                try
                {
                    var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket
                        ?? throw new IOException("The connection has no Unix socket.");
                    UnixPeerCredentials.RequireSameUser(socket);
                    connection.Features.Set(new Peer(UnixPeerCredentials.User));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    connection.Abort(new ConnectionAbortedException("Control peer authentication failed.", ex));
                    return;
                }
                await next(connection);
            }));
        });
    }

    internal static bool Authorized(HttpContext context)
        => context.Features.Get<Peer>() is { } peer && peer.User == UnixPeerCredentials.User;
}
