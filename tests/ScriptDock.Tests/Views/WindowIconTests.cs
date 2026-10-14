using System;
using Avalonia.Headless.XUnit;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

public sealed class WindowIconTests
{
    // The Windows taskbar and Alt+Tab icon; macOS shows the app bundle's icon instead.
    [AvaloniaFact]
    public void On_Windows_the_app_windows_carry_the_app_icon()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs on Windows only.");
        Assert.NotNull(new MainWindow().Icon);
        Assert.NotNull(new RecordsWindow().Icon);
    }
}
