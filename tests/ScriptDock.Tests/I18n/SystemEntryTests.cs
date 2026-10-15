using Avalonia.Headless.XUnit;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// A choice that follows something outside the app names what it resolves to now, the word and the value
/// both in the interface language and in its punctuation, so Japanese and Chinese use full-width brackets; a zone name appears as is (localization-conventions, Translation).
/// </summary>
public sealed class SystemEntryTests
{
    // Speaks one language with a given computer language list for the test, then restores both.
    private static void With(string language, string[] computer, System.Action check)
    {
        var preference = Localizer.Preference;
        var previous = Localizer.ComputerLanguages;
        Localizer.Use(language, computer);
        try { check(); }
        finally { Localizer.Use(preference, previous); }
    }

    [AvaloniaFact]
    public void The_language_entry_names_the_resolved_language_in_the_interface_language()
    {
        With("en", ["ja-JP"], () =>
            Assert.Equal("System (Japanese)", new SettingsDialogViewModel(new AppConfig()).LanguageOptions[0].Name));
        With("ja", ["ja-JP"], () =>
            Assert.Equal("システム（日本語）", new SettingsDialogViewModel(new AppConfig()).LanguageOptions[0].Name));
        With("de", ["fr-FR"], () =>
            Assert.Equal("System (Französisch)", new SettingsDialogViewModel(new AppConfig()).LanguageOptions[0].Name));
    }

    [AvaloniaFact]
    public void The_theme_entry_names_the_appearance_in_the_interface_language()
    {
        With("ja", ["ja-JP"], () =>
        {
            var draft = new SettingsDialogViewModel(new AppConfig()) { SystemThemeIsDark = true };
            Assert.Equal("システム（ダーク）", draft.ThemeSystemLabel);
        });
    }

    [AvaloniaFact]
    public void The_zone_entry_shows_the_zone_name_as_is()
    {
        With("ja", ["ja-JP"], () =>
        {
            var zone = ScriptDock.Services.DisplayTime.NameOf(System.TimeZoneInfo.Local);
            Assert.Equal($"システム（{zone}）", new SettingsDialogViewModel(new AppConfig()).TimeZoneOptions[0].Name);
        });
    }

    [Fact]
    public void Every_language_has_a_name_in_every_interface_language()
    {
        foreach (var tag in Languages.Tags)
            Assert.StartsWith("language.", Languages.InterfaceNameKey(tag));
        Assert.Equal("language.ja", Languages.InterfaceNameKey("ja"));
    }
}
