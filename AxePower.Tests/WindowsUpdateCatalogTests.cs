using AxePower.Core.Services;

namespace AxePower.Tests;

public sealed class WindowsUpdateCatalogTests
{
    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelOrDuplicateTheWindowsSearch()
    {
        using var cancelled = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim(false);
        var expected = new WindowsUpdateScanResult([new("Pending patch", "Windows Update catalog", false)], true, false);
        int calls = 0;
        var service = new WindowsUpdateCatalogService(new LogService(), () =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            if (!finish.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test did not release the scan.");
            return expected;
        });

        Task<WindowsUpdateScanResult> first = service.ScanAsync(cancelled.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));

            Task<WindowsUpdateScanResult> second = service.ScanAsync();
            finish.Set();

            Assert.Same(expected, await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, calls);
        }
        finally
        {
            finish.Set();
        }
    }

    [Fact]
    public async Task CompletedScanCanBeRetriedAndPartialResultsRemainVisible()
    {
        int calls = 0;
        var patch = new WindowsUpdateCandidate("Pending patch", "Optional", false, IsOptional: true);
        var service = new WindowsUpdateCatalogService(new LogService(), () =>
            Interlocked.Increment(ref calls) == 1
                ? new WindowsUpdateScanResult([patch], false, true, "Partial results.")
                : new WindowsUpdateScanResult([], true, null));

        var partial = await service.ScanAsync();
        Assert.False(partial.Succeeded);
        Assert.True(partial.RebootRequired);
        Assert.Same(patch, Assert.Single(partial.Candidates));

        var retry = await service.ScanAsync();
        Assert.True(retry.Succeeded);
        Assert.Null(retry.RebootRequired);
        Assert.Empty(retry.Candidates);
        Assert.Equal(2, calls);
    }
}
