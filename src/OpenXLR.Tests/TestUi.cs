using Avalonia.Threading;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("OpenXLR.Tests.TestUiFramework", "OpenXLR.Tests")]

namespace OpenXLR.Tests;

/// <summary>
/// The unit test process's UI thread. Avalonia gives the UI dispatcher to the
/// first thread that asks for it, for the life of the process. In the UI that
/// is the main thread, before anything else runs; in a test process it would
/// be whichever thread got there first, and a model's continuation that posts
/// its result to the UI from the thread pool, as the plugin catalogue does
/// when its request fails, could take it from a pool thread that never runs
/// it. A binding made on any other thread then waits for that thread forever.
/// So this thread asks first, before any test runs (see
/// <see cref="TestUiFramework"/>), and runs the dispatcher for the whole run:
/// what models post runs here, as in the UI, and a test that binds controls
/// does it here through <see cref="Run"/>.
/// </summary>
/// <remarks>
/// The window fixtures start the X11 platform on a thread of their own, and
/// the platform must be the first to set up the UI dispatcher, so this
/// thread stays out of a run that enables one of them.
/// </remarks>
internal static class TestUi
{
    private static readonly string[] WindowFixtures =
    [
        "OPENXLR_TEST_LAYOUT", "OPENXLR_TEST_TOOLTIP", "OPENXLR_TEST_SKIN",
        "OPENXLR_TEST_EQ", "OPENXLR_TEST_CAPTURE", "OPENXLR_TEST_DESKTOP",
    ];

    private static readonly object Gate = new();
    private static bool _started;

    /// <summary>Take the UI dispatcher on a thread of its own and keep it running; once per process.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            if (_started || WindowFixtures.Any(name => Environment.GetEnvironmentVariable(name) == "1")) return;
            _started = true;
        }
        using var owned = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.UIThread;
            // Posted work no test waits for writes its failure to the test
            // output instead of ending the run.
            dispatcher.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"Unhandled on the test UI thread: {e.Exception}");
                e.Handled = true;
            };
            owned.Set();
            dispatcher.MainLoop(CancellationToken.None);
        }) { IsBackground = true, Name = "Test UI" };
        thread.Start();
        owned.Wait();
    }

    /// <summary>Run work that binds or drives controls on the UI thread, and wait for it.</summary>
    internal static void Run(Action work) => Dispatcher.UIThread.Invoke(work);
}

/// <summary>
/// xUnit's framework, with the test UI thread started when the framework is
/// made: before discovery and before any test, so no test can take the UI
/// dispatcher first. A module initializer cannot do it, since the new thread
/// runs this assembly's code and would wait for the initializer to finish.
/// </summary>
public sealed class TestUiFramework : XunitTestFramework
{
    public TestUiFramework(IMessageSink messageSink) : base(messageSink) => TestUi.Start();
}
