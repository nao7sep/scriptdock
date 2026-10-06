using ScriptDock.Models;
using ScriptDock.Services;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Finalisation invariants for a run that never owns a real OS process (so these stay pure): the
/// once-only latch means a run is finalised once and cannot re-raise StateChanged.
/// </summary>
public sealed class ScriptProcessTests
{
    private static ScriptProcess New() => new(1, "/x/run.command", default);

    [Fact]
    public void StateChanged_FiresExactlyOnce_AcrossRepeatedCompletes()
    {
        var process = New();
        var raised = 0;
        process.StateChanged += (_, _) => raised++;

        process.Complete(); // no live process → Exited
        process.Complete(); // must be ignored: already finalised

        Assert.Equal(RunState.Exited, process.State);
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
