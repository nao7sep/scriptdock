using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// Tests that read or set this process's AppleLanguages run one at a time: the bootstrap's volatile
/// domain changes what <c>NSLocale.preferredLanguages</c> answers for the whole process.
/// </summary>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class AppKitLanguages
{
    public const string CollectionName = "AppKit languages";
}
