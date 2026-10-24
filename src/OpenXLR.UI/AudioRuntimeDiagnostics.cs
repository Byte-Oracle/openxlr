using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OpenXLR.UI;

internal sealed record AudioProcessDiagnostics(string Service, LinuxProcessResourceSnapshot? Resources,
    string? Note, IReadOnlyList<string> Warnings);

/// <summary>Compare loaded user-unit settings with the processes actually running, without collecting their ids.</summary>
internal static class AudioRuntimeDiagnostics
{
    private static readonly (string Unit, string Process)[] Services =
    [
        ("openxlr-daemon.service", "openxlr-daemon"),
        ("pipewire.service", "pipewire"),
        ("pipewire-pulse.service", "pipewire-pulse"),
        ("wireplumber.service", "wireplumber"),
    ];

    internal static IReadOnlyList<AudioProcessDiagnostics> Read(string units, string proc = "/proc")
    {
        var blocks = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string block in units.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in block.Split('\n'))
            {
                int equals = line.IndexOf('=');
                if (equals > 0) values[line[..equals]] = line[(equals + 1)..];
            }
            if (values.TryGetValue("Id", out string? id)) blocks[id] = values;
        }
        string? uid = LinuxProcessResources.RealUid(Path.Combine(proc, "self"));
        var result = new List<AudioProcessDiagnostics>();
        foreach (var service in Services)
        {
            LinuxProcessResourceSnapshot? resources = null;
            var warnings = new List<string>();
            if (uid is not null && blocks.TryGetValue(service.Unit, out var values)
                && int.TryParse(values.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            {
                resources = LinuxProcessResources.Inspect(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture)),
                    service.Process, uid, descriptorCap: 1024 * 1024);
                if (resources?.OpenFileSoftLimit is long soft && soft >= 0)
                {
                    if (resources.OpenFiles is { } used && used >= soft - soft / 4)
                        warnings.Add("Open files are at least 75% of the running process's soft limit.");
                    if (long.TryParse(values.GetValueOrDefault("LimitNOFILESoft"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out long configured) && configured != soft)
                        warnings.Add("The running soft open-file limit differs from the loaded unit setting. Reloading a unit does not change an already running process's limit.");
                }
            }
            string? note = resources is null ? "No matching same-user main process was readable. The unit may be stopped, absent, managed elsewhere or hidden by the sandbox."
                : resources.OpenFiles is null || resources.OpenFileSoftLimit is null
                    ? "Some process resource fields were unavailable." : null;
            result.Add(new(service.Unit, resources, note, warnings));
        }
        return result;
    }
}
