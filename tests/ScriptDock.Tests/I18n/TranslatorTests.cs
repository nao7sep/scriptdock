using System;
using System.Globalization;
using ScriptDock.I18n;
using Xunit;

namespace ScriptDock.Tests.I18n;

/// <summary>
/// The translator and the pieces it stands on: how a preference resolves, which plural form a count
/// takes, and what happens when a key or a value is missing.
/// </summary>
public class TranslatorTests
{
    [Theory]
    [InlineData(null, Languages.System)]
    [InlineData("", Languages.System)]
    [InlineData("   ", Languages.System)]
    [InlineData("klingon", Languages.System)]
    [InlineData("SYSTEM", Languages.System)]
    [InlineData("ja", "ja")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData(" zh-Hans ", "zh-Hans")]
    public void a_saved_preference_is_read_forgivingly(string? saved, string expected) =>
        Assert.Equal(expected, Languages.NormalizePreference(saved));

    [Theory]
    [InlineData("ja-JP", "ja")]
    [InlineData("zh-Hant-TW", "zh-Hans")]
    [InlineData("zh-HK", "zh-Hans")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("es-419", "es")]
    [InlineData("en_US", "en")]
    [InlineData("nl-NL", "en")]
    public void a_computer_language_resolves_to_the_variety_the_app_ships(string computer, string expected) =>
        Assert.Equal(expected, Languages.Match([computer]));

    [Fact]
    public void system_takes_the_first_language_in_the_set()
    {
        Assert.Equal("de", Languages.Match(["nl-NL", "de-AT", "ja"]));
        Assert.Equal("en", Languages.Match(["nl-NL", "pl-PL"]));
        Assert.Equal("en", Languages.Match([]));
    }

    [Theory]
    // Japanese, Korean and Chinese do not change the noun with the number.
    [InlineData("ja", 1, Plural.Other)]
    [InlineData("zh-Hans", 5, Plural.Other)]
    // English and German split at one.
    [InlineData("en", 1, Plural.One)]
    [InlineData("en", 0, Plural.Other)]
    [InlineData("de", 2, Plural.Other)]
    // French and Brazilian Portuguese count zero with the singular.
    [InlineData("fr", 0, Plural.One)]
    [InlineData("pt-BR", 1, Plural.One)]
    [InlineData("es", 0, Plural.Other)]
    // The Romance "many" form is for round millions.
    [InlineData("it", 1_000_000, Plural.Many)]
    [InlineData("fr", 2_000_000, Plural.Many)]
    [InlineData("it", 1_000_001, Plural.Other)]
    // Russian changes at one, at two to four, and again above.
    [InlineData("ru", 1, Plural.One)]
    [InlineData("ru", 21, Plural.One)]
    [InlineData("ru", 11, Plural.Many)]
    [InlineData("ru", 3, Plural.Few)]
    [InlineData("ru", 24, Plural.Few)]
    [InlineData("ru", 14, Plural.Many)]
    [InlineData("ru", 5, Plural.Many)]
    [InlineData("ru", 0, Plural.Many)]
    public void a_count_takes_its_languages_plural_form(string tag, long count, string expected) =>
        Assert.Equal(expected, Plural.CategoryFor(tag, count));

    [Fact]
    public void every_language_declares_the_forms_it_selects()
    {
        // Whatever the count, the form chosen must be one the catalogue is required to carry.
        foreach (var tag in Languages.Tags)
        {
            var categories = Plural.CategoriesOf(tag);
            for (long count = 0; count <= 200; count++)
                Assert.Contains(Plural.CategoryFor(tag, count), categories);
            foreach (var count in new long[] { 1_000_000, 2_000_000, 1_000_001, 999_999 })
                Assert.Contains(Plural.CategoryFor(tag, count), categories);
        }
    }

    [Fact]
    public void a_missing_key_shows_as_its_key()
    {
        var translator = new Translator("en", CultureInfo.GetCultureInfo("en"));
        Assert.Equal("nothing.here", translator.T("nothing.here"));
    }

    [Fact]
    public void a_language_missing_a_key_falls_back_to_english()
    {
        // Every catalogue has every key today, and the gate keeps it that way; this is what a reader
        // would get if one ever slipped through.
        var japanese = new Translator("ja", CultureInfo.GetCultureInfo("ja"));
        Assert.Equal(
            new Translator("en", CultureInfo.GetCultureInfo("en")).T("nothing.here"),
            japanese.T("nothing.here"));
    }

    [Fact]
    public void a_value_is_filled_in_and_a_number_is_formatted_for_the_reader()
    {
        var german = new Translator("de", CultureInfo.GetCultureInfo("de"));
        Assert.Contains("1.234", german.T("error.count", ("count", 1234)));

        var english = new Translator("en", CultureInfo.GetCultureInfo("en"));
        Assert.Contains("1,234", english.T("error.count", ("count", 1234)));
    }

    [Fact]
    public void an_unfilled_placeholder_is_left_as_it_is()
    {
        // Better a visible {name} than a sentence with a hole in it.
        var english = new Translator("en", CultureInfo.GetCultureInfo("en"));
        Assert.Contains("{name}", english.T("stop.message"));
    }

    [Fact]
    public void a_value_can_be_another_message_in_the_same_language()
    {
        // The scan result is two counted sentences joined by a third entry, so each one keeps its own
        // plural form: one script added and three removed must not agree with the same number.
        var english = new Translator("en", CultureInfo.GetCultureInfo("en"));
        var line = english.T(
            "scan.addedAndRemoved",
            ("added", Message.Of("scan.added", ("count", 1))),
            ("removed", Message.Of("scan.removed", ("count", 3))));

        Assert.Contains("1 new script.", line);
        Assert.Contains("3 scripts no longer found.", line);
    }

    [Fact]
    public void the_formatting_culture_follows_the_computer_when_it_speaks_the_language()
    {
        // A German computer set to German keeps its own regional format.
        var computer = CultureInfo.GetCultureInfo("de-AT");
        Assert.Equal(computer, Languages.FormattingCulture("de", computer));

        // A German-speaking app on a Japanese computer uses German's own format, not Japan's.
        Assert.Equal(
            CultureInfo.GetCultureInfo("de"),
            Languages.FormattingCulture("de", CultureInfo.GetCultureInfo("ja-JP")));
    }

    [Fact]
    public void a_moment_is_shown_in_the_readers_own_order()
    {
        var moment = new DateTimeOffset(2026, 3, 4, 17, 5, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.Utc;

        var american = new Translator("en", CultureInfo.GetCultureInfo("en-US")).DateAndMinute(moment, zone);
        var german = new Translator("de", CultureInfo.GetCultureInfo("de-DE")).DateAndMinute(moment, zone);

        Assert.StartsWith("3/4/2026", american);
        Assert.StartsWith("04.03.2026", german);
    }
}
