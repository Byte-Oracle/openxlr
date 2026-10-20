using System.Text.RegularExpressions;

namespace OpenXLR.Tests;

public sealed class TestStreamPinTests
{
    /// <summary>
    /// The session manager marks a stream that it linked to the default device
    /// as following the default, moves it when the default changes, and
    /// WirePlumber 0.4 can leave the mark behind for the next node that gets the
    /// same id. It never marks a stream in a link group, so every test player
    /// and recorder joins one (a group per role: a recorder must not share the
    /// group of the player it listens to through a filter). Only a stream that
    /// stands in for an application, which OpenXLR itself moves, stays out.
    /// </summary>
    [Fact]
    public void EveryTestPlayerAndRecorderIsKeptOnItsTarget()
    {
        string tests = Path.Combine(Root(), "src", "OpenXLR.Tests");
        var unpinned = new List<string>();
        int launches = 0;
        foreach (string file in Directory.GetFiles(tests, "*.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match launch = Regex.Match(lines[i], @"'pw-cat', '--(record|playback)'");
                if (!launch.Success) continue;
                launches++;
                string statement = lines[i] + (i + 1 < lines.Length ? lines[i + 1] : "");
                if (statement.Contains("application.name =", StringComparison.Ordinal)) continue;
                if (!lines[i].Contains($"node.link-group = openxlr-test-{launch.Groups[1].Value} ", StringComparison.Ordinal))
                    unpinned.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        Assert.True(launches > 5, "no pw-cat launches found under " + tests);
        Assert.Empty(unpinned);
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
