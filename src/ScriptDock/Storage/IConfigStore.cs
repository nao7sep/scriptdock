using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Storage;

public interface IConfigStore
{
    AppConfig Load();

    /// <summary>Writes the file from <paramref name="value"/>: every set that differs from its built-in, whole.</summary>
    Task SaveAsync(AppConfig value);
}
