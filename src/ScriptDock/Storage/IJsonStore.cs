using System.Threading.Tasks;

namespace ScriptDock.Storage;

/// <summary>
/// Load/save contract for a JSON-backed document. Exists so callers can depend on the
/// persistence behaviour without binding to <see cref="JsonStore{T}"/>'s file I/O,
/// which keeps orchestration unit-testable with in-memory fakes.
/// </summary>
public interface IJsonStore<T> where T : class, new()
{
    /// <summary>
    /// Whether the persisted document already exists — i.e. this is not a first run.
    /// Lets callers seed defaults on genuine first use without re-seeding a document
    /// the user has deliberately emptied.
    /// </summary>
    bool Exists { get; }

    T Load();

    /// <summary>Blocking save, for the startup path (before the window exists) and tests.</summary>
    void Save(T value);

    /// <summary>
    /// Queues the write and returns a task that completes when it lands. A caller mutates its own
    /// shared document object in place and calls this on the UI thread, so the JSON snapshot is
    /// taken synchronously (fast, in-memory) at call time; only the actual file and backup I/O runs
    /// off the calling thread. Every store serializes its own writes and runs them in the order they
    /// were queued, so two overlapping calls can never interleave on disk and a write queued earlier
    /// can never land after — and so overwrite — one queued later. Awaiting the task returned by the
    /// most recently queued call also waits for every write queued before it on the same store.
    /// </summary>
    Task SaveAsync(T value);
}
