using System;
using System.Threading;

namespace ScriptDock.Services;

internal sealed class ActivationRequestRouter
{
    private readonly object _gate = new();
    private Action? _handler;
    private bool _pending;

    public void Register(Action handler)
    {
        var invoke = false;
        lock (_gate)
        {
            _handler = handler;
            invoke = _pending;
            _pending = false;
        }
        if (invoke)
            handler();
    }

    public void Request()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
                _pending = true;
        }
        handler?.Invoke();
    }
}

/// <summary>Process-wide ownership of ScriptDock's state, backup, and run-log namespace.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = "ScriptDock-4b2f6f45-33e0-4780-a42a-df1a98321dc5";
    private const string ActivationEventName = "ScriptDock-activate-4b2f6f45-33e0-4780-a42a-df1a98321dc5";
    private static readonly object CurrentGate = new();
    private static SingleInstanceLease? _current;

    private Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly EventWaitHandle? _stopEvent;
    private readonly Thread? _activationThread;
    private readonly ActivationRequestRouter _activationRouter = new();

    private SingleInstanceLease(Mutex mutex, EventWaitHandle? activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        if (activationEvent is null)
            return;

        _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        _activationThread = new Thread(ListenForActivation) { IsBackground = true, Name = "ScriptDock activation" };
        _activationThread.Start();
    }

    public static bool TryAcquire(out SingleInstanceLease? lease)
    {
        EventWaitHandle? activationEvent = null;
        if (OperatingSystem.IsWindows())
            activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

        var mutex = new Mutex(initiallyOwned: false, MutexName);
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
        lock (CurrentGate)
            _current = lease;
        return true;
    }

    public static void RegisterOwnerActivationHandler(Action handler)
    {
        SingleInstanceLease? current;
        lock (CurrentGate)
            current = _current;
        current?._activationRouter.Register(handler);
    }

    private void ListenForActivation()
    {
        var activationEvent = _activationEvent!;
        var stopEvent = _stopEvent!;
        while (WaitHandle.WaitAny([activationEvent, stopEvent]) == 0)
            _activationRouter.Request();
    }

    public void Dispose()
    {
        lock (CurrentGate)
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        }

        _stopEvent?.Set();
        _activationThread?.Join(TimeSpan.FromSeconds(2));
        _activationEvent?.Dispose();
        _stopEvent?.Dispose();

        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
            return;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
