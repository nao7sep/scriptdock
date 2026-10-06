using System.IO;
using System.Threading.Tasks;
using ScriptDock.Models;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

public sealed class FakeConfigStore : IConfigStore
{
    public AppConfig Value { get; set; } = new();
    public bool ThrowOnSave { get; set; }
    public int SaveCount { get; private set; }
    public AppConfig? LastSaved { get; private set; }

    public AppConfig Load() => Value;

    /// <summary>When set, <see cref="SaveAsync"/> returns this gate's task instead of saving: a save
    /// still running until the test settles it.</summary>
    public TaskCompletionSource? SaveGate { get; set; }

    public Task SaveAsync(AppConfig value)
    {
        if (SaveGate is { } gate)
            return gate.Task;
        if (ThrowOnSave)
            return Task.FromException(new IOException("save failed (test)"));
        SaveCount++;
        Value = LastSaved = value;
        return Task.CompletedTask;
    }
}
