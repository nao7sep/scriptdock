using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// Waits for a condition the code under test reports through change events: it checks the condition at
/// once, then again on each change. There is no deadline: the test's own cancellation ends a wait.
/// </summary>
internal static class Wait
{
    /// <summary>
    /// Completes once <paramref name="done"/> holds, re-checked on every change the
    /// <paramref name="sources"/> raise: view models, collections and windows. In an interface test, await it
    /// so the dispatcher runs the work that posts back to the interface thread.
    /// </summary>
    public static async Task UntilAsync(Func<bool> done, params object[] sources)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            try { if (done()) met.TrySetResult(); }
            catch (Exception ex) { met.TrySetException(ex); }
        }

        PropertyChangedEventHandler onProperty = (_, _) => Check();
        NotifyCollectionChangedEventHandler onCollection = (_, _) => Check();
        foreach (var source in sources)
        {
            if (source is INotifyPropertyChanged properties) properties.PropertyChanged += onProperty;
            if (source is INotifyCollectionChanged collection) collection.CollectionChanged += onCollection;
        }

        try
        {
            Check();
            await met.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            foreach (var source in sources)
            {
                if (source is INotifyPropertyChanged properties) properties.PropertyChanged -= onProperty;
                if (source is INotifyCollectionChanged collection) collection.CollectionChanged -= onCollection;
            }
        }
    }

    /// <summary>
    /// Completes once <paramref name="done"/> holds for <paramref name="path"/>, a file another process
    /// writes, re-checked on every change the operating system reports for it. A read that meets the file
    /// mid-write counts as not yet; the write's next change checks again.
    /// </summary>
    public static async Task ForFileAsync(string path, Func<bool> done)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            try { if (done()) met.TrySetResult(); }
            catch (IOException) { }
            catch (Exception ex) { met.TrySetException(ex); }
        }

        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += (_, _) => Check();
        watcher.Changed += (_, _) => Check();
        watcher.Renamed += (_, _) => Check();
        watcher.EnableRaisingEvents = true;

        Check();
        await met.Task.WaitAsync(TestContext.Current.CancellationToken);
    }
}
