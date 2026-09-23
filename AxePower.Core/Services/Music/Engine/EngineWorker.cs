using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Music;

/// <summary>
/// One dedicated MTA thread that owns every decoder and output object. Media Foundation and
/// WASAPI COM objects break across apartments, so they are created, controlled and disposed
/// here only — never on the WPF UI thread.
/// </summary>
internal sealed class EngineWorker : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public EngineWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task RunAsync(Action action) => RunAsync(() => { action(); return true; });

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Work()
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }

        if (IsCurrentThread) Work();
        else if (!TryAdd(Work)) tcs.SetException(new ObjectDisposedException(nameof(EngineWorker)));
        return tcs.Task;
    }

    /// <summary>Fire-and-forget; exceptions are swallowed by the caller-supplied action.</summary>
    public void Post(Action action)
    {
        if (IsCurrentThread) action();
        else TryAdd(action);
    }

    private bool TryAdd(Action action)
    {
        try
        {
            _work.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false; // shutting down
        }
    }

    private void Run()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch { /* RunAsync reports its own exceptions; Post callers handle theirs */ }
        }
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(3));
        _work.Dispose();
    }
}

/// <summary>Raises events one at a time, in order, off the render and worker threads.</summary>
internal sealed class SerialEventQueue
{
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;

    public void Enqueue(Action action)
    {
        lock (_lock)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { action(); } catch { /* a faulty subscriber must not stop later events */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
