using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Storage;

public interface IConfigStore
{
    AppConfig Load();

    /// <summary>The stored keys the last <see cref="Load"/> could not apply, which saves keep unchanged.</summary>
    IReadOnlyList<string> KeptKeys { get; }

    /// <summary>Writes the file from <paramref name="value"/>: every set that differs from its built-in, whole.
    /// A kept entry is written back unchanged unless its key is in <paramref name="replaced"/>, the sets the
    /// user saved a value for, the built-in included.</summary>
    Task SaveAsync(AppConfig value, IReadOnlyCollection<string>? replaced = null);
}
