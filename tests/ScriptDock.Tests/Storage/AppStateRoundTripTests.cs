using System;
using System.Text.Json;
using ScriptDock.Models;
using ScriptDock.Storage;
using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Confirms <see cref="AppState"/> survives the real serializer options (<see
/// cref="JsonOptions.Default"/>), including the Phase 1 additions, and that timestamps
/// land on disk in the conventional millisecond-<c>Z</c> form.
/// </summary>
public sealed class AppStateRoundTripTests
{
    [Fact]
    public void RoundTrips_WindowAndRecentRuns()
    {
        var state = new AppState
        {
            ShowHidden = true,
            KnownPaths = { "/a/x.command", "/b/y.command" },
            Window = new WindowBounds { X = 10, Y = 20, Width = 900, Height = 600 },
            RecentlyRun =
            {
                new RecentRun
                {
                    Path = "/a/x.command",
                    RanAt = new DateTimeOffset(2026, 6, 17, 0, 15, 41, 123, TimeSpan.Zero),
                },
            },
        };

        var json = JsonSerializer.Serialize(state, JsonOptions.Default);
        var back = JsonSerializer.Deserialize<AppState>(json, JsonOptions.Default)!;

        Assert.True(back.ShowHidden);
        Assert.Equal(state.KnownPaths, back.KnownPaths);
        Assert.NotNull(back.Window);
        Assert.Equal(900, back.Window!.Width);
        Assert.Single(back.RecentlyRun);
        Assert.Equal("/a/x.command", back.RecentlyRun[0].Path);
        Assert.Equal(state.RecentlyRun[0].RanAt, back.RecentlyRun[0].RanAt);
    }

    [Fact]
    public void RecentRun_Timestamp_StoredAsIsoMillisZ()
    {
        var state = new AppState
        {
            RecentlyRun =
            {
                new RecentRun
                {
                    Path = "/a/x.command",
                    // 09:15:41.123 +09:00 must persist as 00:15:41.123Z.
                    RanAt = new DateTimeOffset(2026, 6, 17, 9, 15, 41, 123, TimeSpan.FromHours(9)),
                },
            },
        };

        var json = JsonSerializer.Serialize(state, JsonOptions.Default);

        Assert.Contains("\"2026-06-17T00:15:41.123Z\"", json);
    }
}
