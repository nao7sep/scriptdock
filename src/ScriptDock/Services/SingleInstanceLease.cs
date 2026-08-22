using System;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>Process-wide ownership of ScriptDock's state, backup, and run-log namespace.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = "ScriptDock-4b2f6f45-33e0-4780-a42a-df1a98321dc5";
    private Mutex? _mutex;

    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(out SingleInstanceLease? lease)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                lease = null;
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The abandoned wait transfers ownership to this process.
        }

        lease = new SingleInstanceLease(mutex);
        return true;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;

        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
