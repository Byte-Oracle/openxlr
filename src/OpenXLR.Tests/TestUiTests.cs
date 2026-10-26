using Avalonia.Threading;

namespace OpenXLR.Tests;

/// <summary>The test UI thread holds the UI dispatcher and runs what is posted to it, whatever thread posts.</summary>
public sealed class TestUiTests
{
    [Fact]
    public async Task TheUiDispatcherBelongsToTheTestUiThreadAndRunsPostedWork()
    {
        Assert.False(Dispatcher.UIThread.CheckAccess());
        bool onUi = false;
        TestUi.Run(() => onUi = Dispatcher.UIThread.CheckAccess());
        Assert.True(onUi);

        // A pool thread posting, as a model's continuation does, does not take the dispatcher.
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() => Dispatcher.UIThread.Post(() => ran.TrySetResult(Dispatcher.UIThread.CheckAccess())));
        Assert.True(await ran.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
