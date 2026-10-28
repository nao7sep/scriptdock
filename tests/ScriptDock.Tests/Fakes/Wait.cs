using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Xunit;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// Waits for something a test cannot be told about directly, such as a file another process writes or a
/// process ending, by checking until it holds. There is no deadline: the test's own cancellation ends a wait.
/// </summary>
internal static class Wait
{
    public static void Until(Func<bool> done)
    {
        var token = TestContext.Current.CancellationToken;
        while (!done())
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(10);
        }
    }

    /// <summary>Runs the dispatcher's jobs between checks, for work that posts back to the interface thread.</summary>
    public static void UntilOnUi(Func<bool> done) => Until(() =>
    {
        Dispatcher.UIThread.RunJobs();
        return done();
    });

    public static async Task UntilAsync(Func<bool> done)
    {
        while (!done())
            await Task.Delay(10, TestContext.Current.CancellationToken);
    }
}
