using System.Text;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class NativeSinkMuteTests
{
    private static readonly HashSet<string> Selected = new(StringComparer.Ordinal) { "headphones" };

    [Theory]
    [InlineData("{\"object.serial\":\"42\",\"object.id\":\"17\"}", "serial:42")]
    [InlineData("{\"object.serial\":42}", "serial:42")]
    [InlineData("{\"object.id\":\"17\"}", "node:17")]
    [InlineData("{}", "pulse:99")]
    public void NativeIdentityKeepsItsNamespaceAndMuteComesFromTheSameSnapshot(string properties, string identity)
    {
        byte[] json = Encoding.UTF8.GetBytes("[{\"index\":99,\"name\":\"headphones\",\"mute\":true,\"properties\":" + properties + "}]");
        var state = Assert.Single(PipeWireAdapter.ParseNativeSinkMutes(json, Selected));
        Assert.Equal("headphones", state.Key);
        Assert.Equal(identity, state.Value.Identity);
        Assert.True(state.Value.Muted);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"index\":99,\"name\":\"headphones\",\"mute\":\"true\"}]")]
    [InlineData("[{\"index\":99,\"name\":\"headphones\",\"mute\":true,\"properties\":[]}]")]
    [InlineData("[{\"name\":\"headphones\",\"mute\":true}]")]
    [InlineData("[{\"index\":99,\"name\":\"headphones\",\"mute\":true,\"mute\":false}]")]
    [InlineData("[{\"index\":1,\"name\":\"headphones\",\"mute\":true},{\"index\":2,\"name\":\"headphones\",\"mute\":false}]")]
    public void MalformedOrAmbiguousNativeSnapshotsCannotBecomeBaselines(string json)
        => Assert.Throws<JsonException>(() => PipeWireAdapter.ParseNativeSinkMutes(Encoding.UTF8.GetBytes(json), Selected));

    [Fact]
    public void OnlyExactSelectedNamesAreReturned()
    {
        byte[] json = Encoding.UTF8.GetBytes("""
            [{"index":1,"name":"Headphones","mute":true},{"index":2,"name":"headphones","mute":false}]
            """);
        var state = Assert.Single(PipeWireAdapter.ParseNativeSinkMutes(json, Selected));
        Assert.Equal("headphones", state.Key);
        Assert.False(state.Value.Muted);
    }

    [Fact]
    public void NativeSnapshotOutputIsBoundedBeforeJsonParsing()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-native-sinks-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), "/usr/bin/head -c 4194305 /dev/zero\n");
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();

            var error = Assert.Throws<InvalidOperationException>(() => adapter.NativeSinkMutes(Selected));

            Assert.Contains("4194304 bytes", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }
}

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void NativeMuteSnapshotsMatchRealPipeWireIdentitiesAndState()
    {
        var pw = new PipeWireAdapter();
        const string name = "native_mute_snapshot";
        uint module = pw.CreateNullSink(name, "Native mute snapshot");
        try
        {
            var selected = new HashSet<string>(StringComparer.Ordinal) { name };
            pw.SetSinkMuted(name, true);
            var native = Assert.Single(pw.NativeSinkMutes(selected));
            Assert.True(native.Value.Muted);
            var registry = Assert.Single(pw.ObservedSinkMutes(selected));
            Assert.Equal(registry.Value.Identity, native.Value.Identity);

            pw.SetSinkMuted(name, false);
            var unmuted = Assert.Single(pw.NativeSinkMutes(selected));
            Assert.Equal(native.Value.Identity, unmuted.Value.Identity);
            Assert.False(unmuted.Value.Muted);
        }
        finally { pw.UnloadModule(module); }
    }
}
