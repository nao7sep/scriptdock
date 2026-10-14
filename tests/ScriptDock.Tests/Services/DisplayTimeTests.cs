using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Tests.Fakes;
using ScriptDock.Tests.I18n;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// The display zone (timestamp-conventions, Time zones): System by default, following the computer; any
/// of the platform's IANA zones chosen from a list; every shown time follows it, and Save applies it.
/// Each test that changes the zone puts System back.
/// </summary>
public sealed class DisplayTimeTests : IDisposable
{
    public void Dispose() => DisplayTime.Use(DisplayTime.System);

    [Fact]
    public void Only_System_or_a_zone_the_platform_knows_is_a_preference()
    {
        Assert.True(DisplayTime.TryParse("system", out var system));
        Assert.Equal(DisplayTime.System, system);
        Assert.True(DisplayTime.TryParse("Asia/Tokyo", out var tokyo));
        Assert.Equal("Asia/Tokyo", tokyo);
        Assert.False(DisplayTime.TryParse("Mars/Olympus_Mons", out _));
        Assert.False(DisplayTime.TryParse(null, out _));
        Assert.False(DisplayTime.TryParse("", out _));
    }

    [Fact]
    public void System_and_an_unknown_zone_resolve_to_the_computers_zone()
    {
        Assert.Equal(TimeZoneInfo.Local, DisplayTime.Resolve(DisplayTime.System));
        Assert.Equal(TimeZoneInfo.Local, DisplayTime.Resolve("Mars/Olympus_Mons"));
        Assert.Equal("Asia/Tokyo", DisplayTime.NameOf(DisplayTime.Resolve("Asia/Tokyo")));
    }

    [Fact]
    public void The_list_is_the_platforms_IANA_zones_by_name_once_each()
    {
        var zones = DisplayTime.Zones();
        Assert.Contains("Asia/Tokyo", zones);
        Assert.Contains("Europe/Berlin", zones);
        Assert.Equal(zones.Distinct(StringComparer.Ordinal).Count(), zones.Count);
        Assert.Equal(zones.Order(StringComparer.Ordinal), zones);
        Assert.All(zones, zone => Assert.True(DisplayTime.TryParse(zone, out _), zone));
    }

    [AvaloniaFact]
    public void Shown_times_follow_the_zone_and_a_change_is_announced()
    {
        var changes = 0;
        void Count() => changes++;
        DisplayTime.Changed += Count;
        try
        {
            DisplayTime.Use("Asia/Tokyo");
            var tokyoRecord = RecordFormat.Time("2026-10-04T00:00:00.000Z");
            var tokyoRecent = new RecentEntry("/x/a.command", "a.command", DateTimeOffset.Parse("2026-10-04T00:00:00Z"), process: null).LastRanDisplay;
            DisplayTime.Use("Europe/London");
            var london = RecordFormat.Time("2026-10-04T00:00:00.000Z");

            // 00:00 UTC is 9:00 in Tokyo and 1:00 in London (summer time), in any clock format.
            Assert.Contains("9:00:00", tokyoRecord);
            Assert.Contains("1:00:00", london);
            Assert.Contains("9:00", tokyoRecent);
            Assert.Equal(2, changes);

            DisplayTime.Use("Europe/London");
            Assert.Equal(2, changes); // the same zone again is no change
        }
        finally
        {
            DisplayTime.Changed -= Count;
        }
    }

    [Fact]
    public void The_setting_defaults_to_System_and_stores_only_a_chosen_zone()
    {
        Assert.Equal(DisplayTime.System, new AppConfig().TimeZone);
        Assert.DoesNotContain("timeZone", ConfigSets.Changed(new AppConfig()).Keys);
        Assert.Equal("\"Asia/Tokyo\"", ConfigSets.Changed(new AppConfig { TimeZone = "Asia/Tokyo" })["timeZone"].GetRawText());

        var read = new AppConfig();
        ConfigSets.Apply(read, "timeZone", JsonSerializer.SerializeToElement("Asia/Tokyo"));
        Assert.Equal("Asia/Tokyo", read.TimeZone);
        // A zone this computer does not know cannot be applied, so the stored value is kept as it is.
        Assert.Throws<JsonException>(() => ConfigSets.Apply(new AppConfig(), "timeZone", JsonSerializer.SerializeToElement("Mars/Olympus_Mons")));
    }

    [AvaloniaFact]
    public void Settings_lists_System_first_with_the_zone_it_resolves_to_now()
    {
        var draft = new SettingsDialogViewModel(new AppConfig { TimeZone = "Asia/Tokyo" });

        Assert.Equal(DisplayTime.System, draft.TimeZoneOptions[0].Value);
        Assert.Equal(English.Of("settings.timeZoneSystem", ("zone", DisplayTime.NameOf(TimeZoneInfo.Local))), draft.TimeZoneOptions[0].Name);
        Assert.Equal(DisplayTime.Zones(), draft.TimeZoneOptions.Skip(1).Select(option => option.Value));
        Assert.Equal("Asia/Tokyo", draft.TimeZone.Value);
        Assert.False(draft.IsDirty);

        draft.TimeZone = draft.TimeZoneOptions[0];
        Assert.True(draft.IsDirty);
        Assert.Equal(DisplayTime.System, draft.ToConfig().TimeZone);
    }

    [AvaloniaFact]
    public void The_System_theme_entry_names_the_appearance_it_resolves_to_now()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        draft.SystemThemeIsDark = true;
        Assert.Equal("System (Dark)", draft.ThemeSystemLabel);
        draft.SystemThemeIsDark = false;
        Assert.Equal("System (Light)", draft.ThemeSystemLabel);
    }

    [AvaloniaFact]
    public async Task Save_applies_the_zone()
    {
        var configStore = new FakeConfigStore();
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), new FakeRecordStore(),
            configStore.Value, stateStore.Value, new KnownPaths(), new ScriptScanner(), new FakeProcessRunner());
        try
        {
            var draft = vm.CreateSettingsDraft();
            draft.TimeZone = draft.TimeZoneOptions.Single(option => option.Value == "Asia/Tokyo");

            Assert.True(await vm.TryApplySettingsAsync(draft));

            Assert.Equal("Asia/Tokyo", DisplayTime.NameOf(DisplayTime.Zone));
            Assert.Equal("Asia/Tokyo", configStore.LastSaved!.TimeZone);
        }
        finally
        {
            await vm.ShutdownAsync();
        }
    }
}
