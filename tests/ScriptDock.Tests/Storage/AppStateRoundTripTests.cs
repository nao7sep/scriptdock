using System;
using System.Text.Json;
using ScriptDock.Models;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Confirms <see cref="AppState"/> survives the real serializer options (<see
/// cref="JsonOptions.Default"/>).
/// </summary>
public sealed class AppStateRoundTripTests
{
    [Fact]
    public void RoundTrips_State()
    {
        var state = new AppState
        {
            ShowHidden = true,
            // The persisted pane intents (Recent column width / console row height) the window
            // restores and re-clamps on load; they must survive the serializer untouched.
            RecentPaneWidth = 420,
            ConsoleHeight = 240,
            WindowPositionX = -1400,
            WindowPositionY = 80,
            WindowWidth = 1100.5,
            WindowHeight = 720.25,
            WindowMaximized = true,
        };

        var json = JsonSerializer.Serialize(state, JsonOptions.Default);
        var back = JsonSerializer.Deserialize<AppState>(json, JsonOptions.Default)!;

        Assert.True(back.ShowHidden);
        Assert.Equal(420, back.RecentPaneWidth);
        Assert.Equal(240, back.ConsoleHeight);
        Assert.Equal(-1400, back.WindowPositionX);
        Assert.Equal(80, back.WindowPositionY);
        Assert.Equal(1100.5, back.WindowWidth);
        Assert.Equal(720.25, back.WindowHeight);
        Assert.True(back.WindowMaximized);
    }

    [Fact]
    public void ObsoleteWindowPlacement_IsDiscardedAndNotSavedAgain()
    {
        var state = JsonSerializer.Deserialize<AppState>("""
            {"showHidden":true,"windowPlacements":{"main":{"normalBounds":{"x":20,"y":40,"width":1200,"height":800},"mode":"maximized"}}}
            """, JsonOptions.Default)!;

        var json = JsonSerializer.Serialize(state, JsonOptions.Default);

        Assert.True(state.ShowHidden);
        Assert.DoesNotContain("windowPlacements", json, StringComparison.Ordinal);
    }
}
