using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ScriptDock.I18n;
using ScriptDock.Storage;

namespace ScriptDock.Models;

/// <summary>The known sets and their shape boundary. Values inside a valid set are kept whole;
/// text is held after the text-cleanup-conventions, paths exactly.</summary>
public static class ConfigSets
{
    private sealed record Set(Func<AppConfig, object> Read, Action<AppConfig, JsonElement> Apply);

    private static readonly IReadOnlyDictionary<string, Set> Sets = new Dictionary<string, Set>
    {
        ["uiFontFamily"] = new(c => c.UiFontFamily, (c, v) => c.UiFontFamily = TextCleanup.SingleLine(String(v))),
        ["language"] = new(c => c.Language, (c, v) =>
            c.Language = Languages.TryParse(String(v), out var language) ? language : throw new JsonException("Unknown language")),
        ["theme"] = new(c => c.Theme, (c, v) =>
        {
            var name = String(v);
            if (int.TryParse(name, out _) || !Enum.TryParse<ThemePreference>(name, true, out var theme) || !Enum.IsDefined(theme))
                throw new JsonException("Unknown theme");
            c.Theme = theme;
        }),
        ["rootDirs"] = new(c => c.RootDirs, (c, v) => c.RootDirs = Strings(v)),
        ["extensions"] = new(c => c.Extensions, (c, v) => c.Extensions = Texts(v)),
        ["ignorePatterns"] = new(c => c.IgnorePatterns, (c, v) => c.IgnorePatterns = Texts(v)),
        ["hidden"] = new(c => c.Hidden, (c, v) => c.Hidden = Strings(v)),
        ["killProcessesOnClose"] = new(c => c.KillProcessesOnClose, (c, v) => c.KillProcessesOnClose = v.GetBoolean()),
        ["recaptureProcessesOnLaunch"] = new(c => c.RecaptureProcessesOnLaunch, (c, v) => c.RecaptureProcessesOnLaunch = v.GetBoolean()),
    };

    public static bool IsKnown(string key) => Sets.ContainsKey(key);
    public static void Apply(AppConfig config, string key, JsonElement value) => Sets[key].Apply(config, value);

    /// <summary>Every set of <paramref name="config"/> that differs from its built-in, whole (config-sets-conventions).</summary>
    public static Dictionary<string, JsonElement> Changed(AppConfig config)
    {
        var builtIn = new AppConfig();
        return Sets.Keys
            .Where(key => Raw(config, key) != Raw(builtIn, key))
            .ToDictionary(key => key, key => Value(config, key));
    }

    public static bool Same(AppConfig a, AppConfig b) => Sets.Keys.All(key => Raw(a, key) == Raw(b, key));

    private static JsonElement Value(AppConfig config, string key) =>
        JsonSerializer.SerializeToElement(Sets[key].Read(config), JsonOptions.Default);

    private static string Raw(AppConfig config, string key) => Value(config, key).GetRawText();

    private static string String(JsonElement value) => value.GetString() ?? throw new JsonException("Expected a string");
    private static List<string> Strings(JsonElement value) => value.EnumerateArray().Select(String).ToList();
    private static List<string> Texts(JsonElement value) => Strings(value).Select(TextCleanup.SingleLine).ToList();
}
