using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ScriptDock;

[assembly: AvaloniaTestApplication(typeof(ScriptDock.Tests.TestAppBuilder))]

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
