using OpenXLR.UI;
using Tmds.DBus.Protocol;
using PrivateBus = OpenXLR.Tests.DesktopKeysTests.PrivateBus;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class FlatpakTrayTests
{
    [DesktopBusFact]
    public async Task TrayRegistersWithoutKdeNameOwnershipAndServesItsMenu()
    {
        await using var environment = await PrivateBus.Start();
        using var desktop = new DBusConnection(environment.Address);
        await desktop.ConnectAsync();
        await desktop.RequestNameAsync(FlatpakTray.Watcher);
        var host = new Host(); desktop.AddMethodHandler(host);
        int shown = 0, quit = 0;
        await using var tray = new FlatpakTray(() => shown++, () => quit++, action => action());
        Assert.Equal(FlatpakTray.Service, await host.First.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var bus = new DesktopBus(desktop);
        var properties = await bus.Call(FlatpakTray.Service, "/StatusNotifierItem", "org.freedesktop.DBus.Properties", "GetAll", "s",
            (ref MessageWriter writer) => writer.WriteString("org.kde.StatusNotifierItem"),
            static (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue());
        Assert.Equal("io.github.emaspa.openxlr", properties["IconName"].GetString());
        Assert.Equal("/MenuBar", properties["Menu"].GetObjectPath().ToString());
        Assert.Equal(0, properties["IconPixmap"].Count);
        Assert.Equal(VariantValueType.Struct, properties["IconPixmap"].ItemType);
        var ids = await bus.Call(FlatpakTray.Service, "/MenuBar", "com.canonical.dbusmenu", "GetLayout", "iias",
            (ref MessageWriter writer) => { writer.WriteInt32(0); writer.WriteInt32(-1); writer.WriteArray(System.Array.Empty<string>()); },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                Assert.Equal(1u, reader.ReadUInt32()); reader.AlignStruct();
                Assert.Equal(0, reader.ReadInt32()); reader.ReadDictionaryOfStringToVariantValue();
                var end = reader.ReadArrayStart(DBusType.Variant);
                var result = new List<int>();
                while (reader.HasNext(end)) result.Add(reader.ReadVariantValue().GetItem(0).GetInt32());
                return result;
            });
        Assert.Equal([1, 2, 3], ids);
        foreach (string method in new[] { "GetGroupProperties", "AboutToShowGroup" })
            await Assert.ThrowsAsync<DBusErrorReplyException>(() => bus.Empty(FlatpakTray.Service, "/MenuBar", "com.canonical.dbusmenu",
                method, method == "GetGroupProperties" ? "aias" : "ai", (ref MessageWriter writer) =>
                {
                    writer.WriteArray(Enumerable.Range(0, 33));
                    if (method == "GetGroupProperties") writer.WriteArray(System.Array.Empty<string>());
                }));
        foreach (int id in new[] { 2, 1, 3, 99 })
            await bus.Empty(FlatpakTray.Service, "/MenuBar", "com.canonical.dbusmenu", "Event", "isvu",
                (ref MessageWriter writer) => { writer.WriteInt32(id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); });
        Assert.Equal(1, shown); Assert.Equal(1, quit);
        var errors = await bus.Call(FlatpakTray.Service, "/MenuBar", "com.canonical.dbusmenu", "EventGroup", "a(isvu)",
            (ref MessageWriter writer) =>
            {
                var array = writer.WriteArrayStart(DBusType.Struct);
                foreach (int id in new[] { 1, 3, 99 })
                { writer.WriteStructureStart(); writer.WriteInt32(id); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); }
                writer.WriteArrayEnd(array);
            }, static (message, _) => message.GetBodyReader().ReadArrayOfInt32());
        Assert.Equal([99], errors); Assert.Equal(2, shown); Assert.Equal(2, quit);
        await Assert.ThrowsAsync<DBusErrorReplyException>(() => bus.Empty(FlatpakTray.Service, "/MenuBar", "com.canonical.dbusmenu", "EventGroup", "a(isvu)",
            (ref MessageWriter writer) =>
            {
                var array = writer.WriteArrayStart(DBusType.Struct);
                for (int i = 0; i < 33; i++)
                { writer.WriteStructureStart(); writer.WriteInt32(1); writer.WriteString("clicked"); writer.WriteVariantInt32(0); writer.WriteUInt32(0); }
                writer.WriteArrayEnd(array);
            }));
        Assert.Equal(2, shown); Assert.Equal(2, quit);
        await desktop.ReleaseNameAsync(FlatpakTray.Watcher);
        await desktop.RequestNameAsync(FlatpakTray.Watcher);
        await host.Again.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Host : IPathMethodHandler
    {
        public string Path => "/StatusNotifierWatcher";
        public bool HandlesChildPaths => false;
        internal readonly TaskCompletionSource<string> First = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Again = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            string service = context.Request.GetBodyReader().ReadString();
            if (Interlocked.Increment(ref _calls) == 1) First.TrySetResult(service);
            else Again.TrySetResult();
            using var reply = context.CreateReplyWriter(null); context.Reply(reply.CreateMessage());
            return ValueTask.CompletedTask;
        }
    }
}
