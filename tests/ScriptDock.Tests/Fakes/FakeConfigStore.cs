using System.Collections.Generic;
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
    public IReadOnlyCollection<string> LastKeys { get; private set; } = [];
    public IReadOnlyCollection<string> LastResetKeys { get; private set; } = [];

    public AppConfig Load() => Value;

    public Task SaveSetsAsync(AppConfig value, IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? resetKeys = null)
    {
        if (ThrowOnSave)
            return Task.FromException(new IOException("save failed (test)"));
        SaveCount++;
        Value = LastSaved = value;
        LastKeys = keys;
        LastResetKeys = resetKeys ?? [];
        return Task.CompletedTask;
    }
}
