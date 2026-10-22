using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMicrophonePauseTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    [InlineData("\"true\"", false)]
    public void TheOptInCommandRequiresABoolean(string value, bool valid)
    {
        using var mixer = new Mixer();
        var command = new Command { Cmd = "setMonitorMuteMicrophones", Value = JsonSerializer.Deserialize<JsonElement>(value) };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void TheOptInAndPausePersistAsMachineStateAndDoNotEnterProfiles()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-mic-pause-settings-").FullName;
        try
        {
            string path = Path.Combine(directory, "mixer.json");
            using var mixer = new Mixer();
            Assert.False(mixer.MonitorMuteMicrophones);
            mixer.SetMonitorMuteMicrophones(true);
            mixer.SetMonitorMuted(true);
            Assert.Null(mixer.ExportSettings().Save(path));
            MixerSettings saved = MixerSettings.Load(path)!;
            Assert.True(saved.MonitorMuteMicrophones);
            Assert.True(saved.MonitorMuted);
            Assert.DoesNotContain("MonitorMuteMicrophones", JsonSerializer.Serialize(mixer.ExportScene()));
            Assert.DoesNotContain("MonitorMuted", JsonSerializer.Serialize(mixer.ExportScene()));
            Assert.Contains("Resume monitoring", Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record")).Message);
            mixer.SoundCheck("xlr1", "stop");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("refuse", false)]
    [InlineData("unconfirmed", false)]
    [InlineData("refuse", true)]
    public void FailedOrUnconfirmedNativePauseAndResumeStayPendingUntilTheSweepRepairsThem(string refusal, bool missingFirst)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-mic-pause-native-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "mute"), "false");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                if [ -f "$dir/lagged" ]; then printf '[]'; exit; fi
                mute=$(/bin/cat "$dir/mute")
                printf '[{"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_xlr1","media.class":"Audio/Sink"},"params":{"Props":[{"channelVolumes":[1,1],"mute":%s}]}}}]' "$mute"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ "$1" = set-sink-mute ]; then
                  if [ -f "$dir/refuse" ]; then exit 1; fi
                  if [ ! -f "$dir/unconfirmed" ]; then
                    if [ "$3" = 1 ]; then echo true > "$dir/mute"; else echo false > "$dir/mute"; fi
                  fi
                elif [ "$1" = get-sink-mute ]; then
                  if [ "$2" = OpenXLR_ch_xlr2 ]; then exit 1; fi
                  if [ "$(/bin/cat "$dir/mute")" = true ]; then echo 'Mute: yes'; else echo 'Mute: no'; fi
                fi
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer();
            Field("_config", new MixerConfig
            {
                Mixes = [], Channels = missingFirst
                    ? [new("xlr2", "Missing mic") { InputPair = 1 }, new("xlr1", "Mic") { InputPair = 0 }]
                    : [new("xlr1", "Mic") { InputPair = 0 }],
            });
            Field("_built", true);
            try
            {
                mixer.SetMonitorMuteMicrophones(true);
                if (!missingFirst)
                {
                    // The native node precedes an empty registry snapshot at startup.
                    File.WriteAllText(Path.Combine(directory, "lagged"), "");
                    mixer.SetMonitorMuted(true);
                    Assert.False(mixer.Snapshot().MicrophonePausePending);
                    Assert.Equal("true", NativeMute());
                    mixer.SyncOwnSinkLevels(out _);
                    Assert.False(mixer.Snapshot().MicrophonePausePending);
                    mixer.SetMonitorMuted(false);
                    Assert.Equal("false", NativeMute());
                    File.Delete(Path.Combine(directory, "lagged"));
                }
                Block();
                mixer.SetMonitorMuted(true);
                Assert.True(mixer.Snapshot().MicrophonePausePending);
                Assert.Equal("false", NativeMute());
                if (missingFirst)
                    Assert.DoesNotContain(File.ReadAllLines(Path.Combine(directory, "calls")), call => call.Contains("OpenXLR_ch_xlr1", StringComparison.Ordinal));
                Unblock();
                mixer.SyncOwnSinkLevels(out _);
                Assert.Equal(missingFirst, mixer.Snapshot().MicrophonePausePending);
                Assert.Equal("true", NativeMute());

                Block();
                mixer.SetMonitorMuted(false);
                Assert.True(mixer.Snapshot().MicrophonePausePending);
                Assert.Equal("true", NativeMute());
                Unblock();
                mixer.SyncOwnSinkLevels(out _);
                Assert.Equal(missingFirst, mixer.Snapshot().MicrophonePausePending);
                Assert.Equal("false", NativeMute());
            }
            finally { Field("_built", false); }

            void Field(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
            string NativeMute() => File.ReadAllText(Path.Combine(directory, "mute")).Trim();
            void Block() => File.WriteAllText(Path.Combine(directory, refusal), "");
            void Unblock() => File.Delete(Path.Combine(directory, refusal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(directory, true);
        }
    }
}
