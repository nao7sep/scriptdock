using System.IO;
using System.Threading.Tasks;
using ScriptDock.Storage;

namespace ScriptDock.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IJsonStore{T}"/>. <see cref="ThrowOnSave"/> simulates a
/// persistence failure so a view model's save-failure rollback can be tested
/// without making a real file unwritable.
/// </summary>
public sealed class FakeJsonStore<T> : IJsonStore<T> where T : class, new()
{
    public T Value { get; set; } = new();
    public bool ThrowOnSave { get; set; }
    public bool Exists { get; set; }
    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }
    public T? LastSaved { get; private set; }

    public T Load()
    {
        LoadCount++;
        return Value;
    }

    public void Save(T value)
    {
        if (ThrowOnSave)
            throw new IOException("save failed (test)");

        SaveCount++;
        LastSaved = value;
        Value = value;
        Exists = true;
    }

    // Runs the same (possibly throwing) Save on a background thread, so a caller that awaits this —
    // the same way it awaits the real JsonStore<T> — observes a faulted task on failure rather than a
    // synchronous throw, matching production semantics for orchestration tests.
    public Task SaveAsync(T value) => Task.Run(() => Save(value));
}
