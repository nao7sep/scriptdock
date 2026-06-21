using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Finalisation invariants for a run that never owns a real OS process (so these stay pure): the
/// once-only latch means whichever of Complete/Fail runs first wins, and the loser cannot overwrite
/// the terminal state or re-raise StateChanged.
/// </summary>
public sealed class ScriptProcessTests
{
    private static ScriptProcess New() => new(1, "/x/run.command", default);

    [Fact]
    public void Fail_SetsFailedState_AndSurfacesMessageAsOutput()
    {
        var process = New();

        process.Fail("Failed to start: boom");

        Assert.Equal(RunState.Failed, process.State);
        Assert.Equal(["Failed to start: boom"], process.ReadOutput());
    }

    [Fact]
    public void Complete_ThenFail_KeepsExited_OnceOnly()
    {
        var process = New();

        process.Complete();           // no live process → Exited
        process.Fail("late failure"); // must be ignored: already finalised

        Assert.Equal(RunState.Exited, process.State);
        Assert.Empty(process.ReadOutput()); // no failure message leaked in
    }

    [Fact]
    public void Fail_ThenComplete_KeepsFailed_OnceOnly()
    {
        var process = New();

        process.Fail("boom");
        process.Complete(); // must be ignored

        Assert.Equal(RunState.Failed, process.State);
        Assert.Equal(["boom"], process.ReadOutput());
    }

    [Fact]
    public void StateChanged_FiresExactlyOnce_AcrossCompleteAndFail()
    {
        var process = New();
        var raised = 0;
        process.StateChanged += (_, _) => raised++;

        process.Complete();
        process.Fail("ignored");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Dispose_WithNoOsProcess_IsSafe_AndIdempotent()
    {
        var process = New();

        process.Dispose();
        process.Dispose(); // second call must not throw
    }
}
