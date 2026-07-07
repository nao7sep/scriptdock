using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ScriptDock;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ScriptDock.Tests.TestAppBuilder))]

// Avalonia headless drives every [AvaloniaFact] through a single shared application and
// dispatcher, which is not safe to run from several test collections at once — xUnit's
// default cross-collection parallelism deadlocks it (the suite hangs with multiple
// AvaloniaFact classes in flight). Serialize the whole assembly: the standard Avalonia
// headless configuration, and the suite is small enough that the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScriptDock.Tests;

/// <summary>
/// Headless Avalonia entry point for the [AvaloniaFact] view tests. It reuses the real <see cref="App"/>
/// so the theme resources load, but the headless lifetime is not a classic desktop one, so the app's
/// own startup (which would create the main window and touch the real storage root) never runs.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
