using OpenXLR.UI;
using Tmds.DBus.Protocol;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DesktopPortalIdentityTests
{
    [DesktopBusTheory]
    [InlineData(null)]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod")]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface")]
    public async Task HostRegistrationPrecedesShortcutsOnTheSameConnection(string? unsupported)
    {
        using var host = new HostIdentity();
        await using var environment = await DesktopKeysTests.PrivateBus.Start();
        using var portal = new DBusConnection(environment.Address);
        await portal.ConnectAsync();
        await portal.RequestNameAsync(DesktopBus.Portal);
        var backend = new RegistryBackend(unsupported);
        portal.AddMethodHandler(backend);
        using var connection = new DBusConnection(environment.Address);
        await connection.ConnectAsync();
        var bus = new DesktopBus(connection);

        await bus.RegisterHostAsync();
        await bus.Empty(DesktopBus.Portal, DesktopBus.PortalPath, DesktopBus.Shortcuts, "CreateSession", "a{sv}",
            (ref MessageWriter writer) => writer.WriteDictionary(new Dictionary<string, VariantValue>()));

        Assert.Equal(["Register", "CreateSession"], backend.Calls);
        Assert.Equal("openxlr", backend.AppId);
        Assert.Equal(connection.UniqueName, backend.RegisteredSender);
    }

    [DesktopBusTheory]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied")]
    [InlineData("org.freedesktop.portal.Error.NotAllowed")]
    [InlineData("org.freedesktop.portal.Error.Failed")]
    [InlineData("org.freedesktop.DBus.Error.UnknownObject")]
    public async Task ARefusedIdentityIsNotTreatedAsAnOlderPortal(string failure)
    {
        using var host = new HostIdentity();
        await using var environment = await DesktopKeysTests.PrivateBus.Start();
        using var portal = new DBusConnection(environment.Address);
        await portal.ConnectAsync();
        await portal.RequestNameAsync(DesktopBus.Portal);
        var backend = new RegistryBackend(failure);
        portal.AddMethodHandler(backend);
        using var connection = new DBusConnection(environment.Address);
        await connection.ConnectAsync();

        var error = await Assert.ThrowsAsync<DBusErrorReplyException>(() => new DesktopBus(connection).RegisterHostAsync());
        Assert.Equal(failure, error.ErrorName);
        Assert.Equal(["Register"], backend.Calls);
    }

    [Fact]
    public async Task FlatpakKeepsTheIdentityProvidedByItsSandbox()
    {
        string? previous = Environment.GetEnvironmentVariable("FLATPAK_ID");
        try
        {
            Environment.SetEnvironmentVariable("FLATPAK_ID", FlatpakEnvironment.AppId);
            // An unconnected client proves the host-only method was not sent.
            using var connection = new DBusConnection("unix:path=/openxlr-test-no-portal");
            await new DesktopBus(connection).RegisterHostAsync();
        }
        finally { Environment.SetEnvironmentVariable("FLATPAK_ID", previous); }
    }

    private sealed class HostIdentity : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("FLATPAK_ID");
        internal HostIdentity() => Environment.SetEnvironmentVariable("FLATPAK_ID", null);
        public void Dispose() => Environment.SetEnvironmentVariable("FLATPAK_ID", _previous);
    }

    private sealed class RegistryBackend(string? error) : IPathMethodHandler
    {
        public string Path => DesktopBus.PortalPath;
        public bool HandlesChildPaths => false;
        internal List<string> Calls { get; } = [];
        internal string? AppId { get; private set; }
        internal string? RegisteredSender { get; private set; }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            string? member = context.Request.MemberAsString;
            var reader = context.Request.GetBodyReader();
            if (context.Request.InterfaceAsString == "org.freedesktop.host.portal.Registry" && member == "Register")
            {
                Calls.Add(member);
                AppId = reader.ReadString();
                Assert.Empty(reader.ReadDictionaryOfStringToVariantValue());
                RegisteredSender = context.Request.SenderAsString;
                if (error is not null)
                {
                    context.ReplyError(error, "The portal rejected registration.");
                    return ValueTask.CompletedTask;
                }
            }
            else if (context.Request.InterfaceAsString == DesktopBus.Shortcuts && member == "CreateSession")
            {
                Calls.Add(member);
                Assert.Equal(RegisteredSender, context.Request.SenderAsString);
                Assert.Empty(reader.ReadDictionaryOfStringToVariantValue());
            }
            else
            {
                context.ReplyUnknownMethodError();
                return ValueTask.CompletedTask;
            }
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
            return ValueTask.CompletedTask;
        }
    }
}
