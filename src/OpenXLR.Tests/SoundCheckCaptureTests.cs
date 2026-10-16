namespace OpenXLR.Tests;

public sealed class SoundCheckCaptureTests
{
    [Theory]
    [InlineData("delayed", .25)]
    [InlineData("zero", 0)]
    public void ASettledCaptureIsMeasuredAfterItsSamplesArrive(string mode, double expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        WithCapture(mode, environment =>
        {
            var result = SoundCheckIntegrationTests.CaptureSound(expected, environment: environment);
            Assert.True(result.Ok, result.StdoutText + result.Stderr);
        });
    }

    [Theory]
    [InlineData("wrong", .25)]
    [InlineData("nonfinite", .25)]
    [InlineData("short", .25)]
    [InlineData("no-data", 0)]
    [InlineData("exit", .25)]
    public void AFailedCaptureStillFailsWithinItsDeadline(string mode, double expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        WithCapture(mode, environment =>
        {
            var result = SoundCheckIntegrationTests.CaptureSound(expected, TimeSpan.FromSeconds(.3), environment);
            Assert.False(result.Ok);
            Assert.False(result.TimedOut); // The assertion, not the outer process guard, ends the capture.
        });
    }

    private static void WithCapture(string mode, Action<IReadOnlyDictionary<string, string>> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-capture-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-cat"), """
                if [ "$1" = --help ]; then printf '%s\n' --raw; exit 0; fi
                exec python3 -c '
                import os, struct, sys, time
                mode = os.environ["OPENXLR_FAKE_CAPTURE"]
                if mode == "exit": sys.exit(2)
                if mode != "no-data":
                    value = .75 if mode == "wrong" else float("nan") if mode == "nonfinite" else 0 if mode in ("zero", "delayed") else .25
                    sys.stdout.buffer.write(struct.pack("<f", value) * (100 if mode == "short" else 48000))
                    sys.stdout.buffer.flush()
                if mode == "delayed":
                    time.sleep(1)
                    sys.stdout.buffer.write(struct.pack("<f", .25) * 48000)
                    sys.stdout.buffer.flush()
                time.sleep(30)
                '
                """);
            check(new Dictionary<string, string>
            {
                ["PATH"] = directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["OPENXLR_FAKE_CAPTURE"] = mode,
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
