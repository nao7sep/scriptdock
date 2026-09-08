using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>
/// Volatile session state, persisted to <c>~/.scriptdock/state.json</c>. Regenerable UI
/// state that should not churn the durable preferences in <see cref="AppConfig"/>.
/// </summary>
/// <remarks>
/// Phase 1 adds the recently-run list; <see cref="KnownPaths"/> is used by the Phase 2
/// scanner to compute the new/removed diff.
/// </remarks>
public sealed class AppState : IJsonNormalizable
{
    /// <summary>Whether hidden scripts are currently shown.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>The set of script paths seen at the last acknowledged scan, against which
    /// the next scan computes its new/removed diff.</summary>
    public List<string> KnownPaths { get; set; } = [];

    /// <summary>Recently-run scripts, held newest-first (sorted on <see cref="RecentRun.RanAt"/>).</summary>
    public List<RecentRun> RecentlyRun { get; set; } = [];

    /// <summary>Persisted width of the Recent pane (the resizable right column); null until first saved.</summary>
    public double? RecentPaneWidth { get; set; }

    /// <summary>Persisted height of the Console pane; null until first saved.</summary>
    public double? ConsoleHeight { get; set; }

    /// <summary>Snapshot of the scripts that were running when this state was last saved, recorded
    /// so a relaunch can recapture them by PID + start-time. Replaced whenever the running set changes.</summary>
    public List<PersistedProcess> RunningProcesses { get; set; } = [];

    [JsonConverter(typeof(WindowPlacementsConverter))]
    public WindowPlacements WindowPlacements { get; set; } = new();

    public void NormalizeAfterLoad()
    {
        KnownPaths = KnownPaths?.OfType<string>().ToList() ?? [];
        RecentlyRun = RecentlyRun?.OfType<RecentRun>()
            .Where(run => !string.IsNullOrEmpty(run.Path)).ToList() ?? [];
        RunningProcesses = RunningProcesses?.OfType<PersistedProcess>()
            .Where(process => process.Pid > 0 && !string.IsNullOrEmpty(process.ScriptPath))
            .ToList() ?? [];
        foreach (var process in RunningProcesses)
            process.LogFilePath ??= string.Empty;
        WindowPlacements ??= new WindowPlacements();
    }
}

public sealed class WindowPlacements
{
    public WindowPlacement? Main { get; set; }
}

public sealed class WindowPlacement
{
    public WindowBounds? NormalBounds { get; set; }
    public string Mode { get; set; } = "normal";
}

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class WindowPlacementsConverter : JsonConverter<WindowPlacements>
{
    public override WindowPlacements Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("main", out var main)
            || main.ValueKind != JsonValueKind.Object)
            return new WindowPlacements();
        var mode = main.TryGetProperty("mode", out var modeValue)
            && modeValue.ValueKind == JsonValueKind.String
            && modeValue.GetString() is "normal" or "maximized"
                ? modeValue.GetString()!
                : "normal";
        WindowBounds? bounds = null;
        if (main.TryGetProperty("normalBounds", out var normal)
            && normal.ValueKind == JsonValueKind.Object
            && TryInt(normal, "x", out var x)
            && TryInt(normal, "y", out var y)
            && TryInt(normal, "width", out var width)
            && TryInt(normal, "height", out var height))
            bounds = new WindowBounds { X = x, Y = y, Width = width, Height = height };
        return new WindowPlacements { Main = new WindowPlacement { NormalBounds = bounds, Mode = mode } };
    }

    public override void Write(Utf8JsonWriter writer, WindowPlacements value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("main");
        JsonSerializer.Serialize(writer, value.Main, options);
        writer.WriteEndObject();
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}

public sealed record DisplayWorkArea(int X, int Y, int Width, int Height, double Scaling);

public static class WindowPlacementPolicy
{
    public static WindowPlacement Resolve(
        WindowPlacement? saved,
        double minimumWidth,
        double minimumHeight,
        IEnumerable<DisplayWorkArea> displays)
    {
        var bounds = saved?.NormalBounds;
        return new WindowPlacement
        {
            Mode = saved?.Mode == "maximized" ? "maximized" : "normal",
            NormalBounds = bounds is not null && IsUsable(bounds, minimumWidth, minimumHeight, displays)
                ? bounds
                : null,
        };
    }

    public static bool IsUsable(
        WindowBounds bounds,
        double minimumWidth,
        double minimumHeight,
        IEnumerable<DisplayWorkArea> displays) => displays.Any(display =>
            bounds.Width >= System.Math.Ceiling(minimumWidth * display.Scaling)
            && bounds.Height >= System.Math.Ceiling(minimumHeight * display.Scaling)
            && bounds.X >= display.X
            && bounds.Y >= display.Y
            && (long)bounds.X + bounds.Width <= (long)display.X + display.Width
            && (long)bounds.Y + bounds.Height <= (long)display.Y + display.Height);
}
