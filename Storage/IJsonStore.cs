namespace ScriptDock.Storage;

/// <summary>
/// Load/save contract for a JSON-backed document. Exists so callers can depend on the
/// persistence behaviour without binding to <see cref="JsonStore{T}"/>'s file I/O,
/// which keeps orchestration unit-testable with in-memory fakes.
/// </summary>
public interface IJsonStore<T> where T : class, new()
{
    /// <summary>
    /// Whether a persisted document (live or backup) already exists — i.e. this is not a
    /// first run. Lets callers seed defaults on genuine first use without re-seeding a
    /// document the user has deliberately emptied.
    /// </summary>
    bool Exists { get; }

    T Load();
    void Save(T value);
}
