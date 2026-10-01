using System.Collections.Generic;
using System.Threading.Tasks;
using ScriptDock.Models;

namespace ScriptDock.Storage;

public interface IConfigStore
{
    AppConfig Load();
    Task SaveSetsAsync(AppConfig value, IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? resetKeys = null);
}
