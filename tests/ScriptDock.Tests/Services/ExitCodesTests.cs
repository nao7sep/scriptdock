using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Services;
using ScriptDock.Storage;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.Services;

/// <summary>
/// Whether an exit code other than 0 reads as an error: off by default, stored only when on, applied on
/// Save, and followed at once by Recent's pills and the Records window. Each test that changes it does so
/// on the UI thread, where the change is announced, and puts the default back there.
/// </summary>
public sealed class ExitCodesTests
{
    private static void Under(bool nonZeroIsError, Action test)
    {
        ExitCodes.Use(nonZeroIsError);
        try { test(); }
        finally { ExitCodes.Use(false); }
    }

    private static RecentEntry Recorded(string state, int? code) =>
        new("/x/a.command", "a.command", DateTimeOffset.UnixEpoch, process: null, lastEnd: new RecordedEnd(state, code));

    [Fact]
    public void The_setting_is_off_by_default_and_stored_only_when_on()
    {
        Assert.False(new AppConfig().NonZeroExitIsError);
        Assert.False(ExitCodes.NonZeroIsError);
        Assert.DoesNotContain("nonZeroExitIsError", ConfigSets.Changed(new AppConfig()).Keys);
        Assert.Equal("true", ConfigSets.Changed(new AppConfig { NonZeroExitIsError = true })["nonZeroExitIsError"].GetRawText());

        var read = new AppConfig();
        ConfigSets.Apply(read, "nonZeroExitIsError", JsonSerializer.SerializeToElement(true));
        Assert.True(read.NonZeroExitIsError);
        // Anything but true or false cannot be applied, so the stored value is kept as it is.
        Assert.Throws<JsonException>(() => ConfigSets.Apply(new AppConfig(), "nonZeroExitIsError", JsonSerializer.SerializeToElement("yes")));
    }

    [AvaloniaFact]
    public void Use_raises_Changed_only_when_the_setting_changes()
    {
        var changes = 0;
        void Count() => changes++;
        ExitCodes.Changed += Count;
        try
        {
            ExitCodes.Use(false);
            ExitCodes.Use(true);
            ExitCodes.Use(true);
            ExitCodes.Use(false);
            Assert.Equal(2, changes);
        }
        finally
        {
            ExitCodes.Changed -= Count;
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Off_a_non_zero_exit_reads_as_a_plain_exit(int code)
    {
        var entry = Recorded("exited", code);

        Assert.Equal(Localizer.T("recent.stateExited"), entry.StatePillText);
        Assert.False(entry.IsPillFailed);
    }

    [AvaloniaFact]
    public void On_a_non_zero_exit_reads_its_code_and_tints_red() => Under(true, () =>
    {
        var entry = Recorded("exited", 3);

        Assert.Equal(Localizer.T("recent.stateExitedWithCode", ("code", 3)), entry.StatePillText);
        Assert.True(entry.IsPillFailed);
        Assert.Equal(Localizer.T("recent.stateExited"), Recorded("exited", 0).StatePillText);
        Assert.False(Recorded("exited", 0).IsPillFailed);
    });

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_run_that_could_not_start_reads_as_failed_either_way(bool nonZeroIsError) => Under(nonZeroIsError, () =>
    {
        var entry = Recorded("failed", null);

        Assert.Equal(Localizer.T("recent.stateFailed"), entry.StatePillText);
        Assert.True(entry.IsPillFailed);
    });

    [AvaloniaFact]
    public void Settings_offers_the_setting_and_a_change_is_a_draft_change()
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        Assert.False(draft.NonZeroExitIsError);
        Assert.False(draft.IsDirty);

        draft.NonZeroExitIsError = true;

        Assert.True(draft.IsDirty);
        Assert.True(draft.ToConfig().NonZeroExitIsError);
        Assert.Contains("nonZeroExitIsError", draft.ChosenSets);
    }

    [AvaloniaFact]
    public async Task Save_applies_the_setting()
    {
        var configStore = new FakeConfigStore();
        var stateStore = new FakeJsonStore<AppState>();
        var vm = new MainWindowViewModel(
            configStore, stateStore, new FakeJsonStore<KnownPaths>(), new FakeJsonStore<HiddenScripts>(), new FakeRecordStore(),
            configStore.Value, stateStore.Value, new KnownPaths(), new HiddenScripts(), new ScriptScanner(), new FakeProcessRunner());
        try
        {
            var draft = vm.CreateSettingsDraft();
            draft.NonZeroExitIsError = true;

            Assert.True(await vm.TryApplySettingsAsync(draft));

            Assert.True(ExitCodes.NonZeroIsError);
            Assert.True(configStore.LastSaved!.NonZeroExitIsError);
            Assert.True(vm.CreateSettingsDraft().NonZeroExitIsError);
        }
        finally
        {
            ExitCodes.Use(false);
            await vm.ShutdownAsync();
        }
    }

    // A run's level is read under the setting, so the window reads its list again for it, and the shown
    // run with it.
    [AvaloniaFact]
    public void The_Records_window_reads_again_under_the_setting()
    {
        var run = new RecordSummary(RecordKind.Run, 4, FakeRecordReader.CurrentSession, "2026-10-04T08:01:00.000Z",
            LogLevel.Info, "/repo/dev.command", null, null);
        var reader = new FakeRecordReader { Page = new RecordsPage([run], false) };
        var stateStore = new FakeJsonStore<AppState>();
        using var vm = new RecordsWindowViewModel(reader, stateStore, stateStore.Value, new FakeTimeProvider());
        vm.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.False(reader.PageQueries.Single().NonZeroExitIsError);

        Under(true, () =>
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, reader.PageQueries.Count);
            Assert.True(reader.PageQueries[^1].NonZeroExitIsError);
        });
    }
}
