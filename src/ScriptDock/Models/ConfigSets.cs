using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ScriptDock.I18n;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>The known sets and their shape boundary. Values inside a valid set are kept whole.</summary>
public static class ConfigSets
{
    public const string Extensions = "extensions";
    public const string IgnorePatterns = "ignorePatterns";
    public const string Hidden = "hidden";

    private sealed record Set(Func<AppConfig, object> Read, Action<AppConfig, JsonElement> Apply);

    private static readonly IReadOnlyDictionary<string, Set> Sets = new Dictionary<string, Set>
    {
        ["uiFontFamily"] = new(c => c.UiFontFamily, (c, v) => c.UiFontFamily = String(v)),
        ["language"] = new(c => c.Language, (c, v) =>
        {
            var language = String(v);
            if (language != Languages.System && !Languages.Tags.Contains(language))
                throw new JsonException("Unknown language");
            c.Language = language;
        }),
        ["theme"] = new(c => c.Theme, (c, v) =>
        {
            var name = String(v);
            if (int.TryParse(name, out _) || !Enum.TryParse<ThemePreference>(name, true, out var theme) || !Enum.IsDefined(theme))
                throw new JsonException("Unknown theme");
            c.Theme = theme;
        }),
        ["rootDirs"] = new(c => c.RootDirs, (c, v) => c.RootDirs = Strings(v)),
        [Extensions] = new(c => c.Extensions, (c, v) => c.Extensions = Strings(v)),
        [IgnorePatterns] = new(c => c.IgnorePatterns, (c, v) => c.IgnorePatterns = Strings(v)),
        [Hidden] = new(c => c.Hidden, (c, v) => c.Hidden = Strings(v)),
        ["killProcessesOnClose"] = new(c => c.KillProcessesOnClose, (c, v) => c.KillProcessesOnClose = v.GetBoolean()),
        ["recaptureProcessesOnLaunch"] = new(c => c.RecaptureProcessesOnLaunch, (c, v) => c.RecaptureProcessesOnLaunch = v.GetBoolean()),
    };

    public static bool IsKnown(string key) => Sets.ContainsKey(key);
    public static void Apply(AppConfig config, string key, JsonElement value) => Sets[key].Apply(config, value);
    public static JsonElement Value(AppConfig config, string key) =>
        JsonSerializer.SerializeToElement(Sets[key].Read(config), JsonOptions.Default);

    public static IReadOnlyCollection<string> ChangedKeys(AppConfig before, AppConfig after) =>
        Sets.Keys.Where(key => Value(before, key).GetRawText() != Value(after, key).GetRawText()).ToArray();

    private static string String(JsonElement value) => value.GetString() ?? throw new JsonException("Expected a string");
    private static List<string> Strings(JsonElement value) => value.EnumerateArray().Select(String).ToList();
}
