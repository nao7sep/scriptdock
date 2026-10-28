using System;
using System.Threading;

namespace ScriptDock.Services;

/// <summary>Process-wide ownership of ScriptDock's state, backup, and run-log namespace.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = "ScriptDock-4b2f6f45-33e0-4780-a42a-df1a98321dc5";
    private const string ActivationEventName = "ScriptDock-activate-4b2f6f45-33e0-4780-a42a-df1a98321dc5";

    // The process's one lease, which the window registers its activation with.
    private static SingleInstanceLease? _current;

    private Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly EventWaitHandle? _stopEvent;
    private Thread? _activationThread;
    private Action? _activate;

    private SingleInstanceLease(Mutex mutex, EventWaitHandle? activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        if (activationEvent is not null)
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
    }

    public static bool TryAcquire(out SingleInstanceLease? lease) =>
        TryAcquire(MutexName, ActivationEventName, out lease);

    internal static bool TryAcquire(string namespaceName, out SingleInstanceLease? lease) =>
        TryAcquire(namespaceName, namespaceName + "-activate", out lease);

    private static bool TryAcquire(string mutexName, string activationName, out SingleInstanceLease? lease)
    {
        EventWaitHandle? activationEvent = null;
        if (OperatingSystem.IsWindows())
            activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                // The shipped Windows loser wakes the owner's listener. The owner restores and
                // activates its existing window before this second process exits.
                activationEvent?.Set();
                activationEvent?.Dispose();
                mutex.Dispose();
                lease = null;
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The abandoned wait transfers ownership to this process.
        }

        lease = new SingleInstanceLease(mutex, activationEvent);
        _current = lease;
        return true;
    }

    /// <summary>
    /// Gives the lease its window's activation and starts listening for second launches, once. The event
    /// stays set for a second launch that arrived earlier, so the listener answers it as soon as it starts.
    /// </summary>
    public static void RegisterOwnerActivationHandler(Action handler)
    {
        if (_current is not { _activationEvent: not null, _activationThread: null } current)
            return;
        current._activate = handler;
        current._activationThread = new Thread(current.ListenForActivation) { IsBackground = true, Name = "ScriptDock activation" };
        current._activationThread.Start();
    }

    private void ListenForActivation()
    {
        var activationEvent = _activationEvent!;
        var stopEvent = _stopEvent!;
        while (WaitHandle.WaitAny([activationEvent, stopEvent]) == 0)
            _activate!();
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this))
            _current = null;

        // The listener only waits on the two events and posts to the window, so it ends as soon as it sees the stop.
        _stopEvent?.Set();
        _activationThread?.Join();
        _activationEvent?.Dispose();
        _stopEvent?.Dispose();

        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
