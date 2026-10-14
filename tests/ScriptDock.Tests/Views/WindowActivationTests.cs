using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Every window the reader opens takes focus in front of the window it came from, and focus never stays
/// behind it (window-conventions, Background visibility and activation). ScriptDock keeps no window on top.
/// The headless platform keeps each window's activation separately, so these observe the window being
/// activated rather than its opener losing focus.
/// </summary>
public sealed class WindowActivationTests : WindowTest
{
    private static RecordsWindowHost Host()
    {
        var stateStore = new FakeJsonStore<AppState>();
        return new RecordsWindowHost(() => new RecordsWindowViewModel(new FakeRecordReader(), stateStore, stateStore.Value, new FakeTimeProvider()));
    }

    [AvaloniaFact]
    public void Records_takes_focus_when_opened_and_again_when_opened_while_behind()
    {
        var opener = Show(new Window());
        var host = Host();

        var records = Track(host.ShowOrActivate(opener));
        var activations = 0;
        records.Activated += (_, _) => activations++;
        Dispatcher.UIThread.RunJobs();
        Assert.True(records.IsActive);

        opener.Activate();
        Dispatcher.UIThread.RunJobs();
        var before = activations;

        Assert.Same(records, host.ShowOrActivate(opener));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before + 1, activations);
    }

    [AvaloniaFact]
    public void Records_opened_from_a_minimized_window_comes_back_when_it_was_minimized_itself()
    {
        var opener = Show(new Window());
        var host = Host();
        var records = Track(host.ShowOrActivate(opener));
        records.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        host.ShowOrActivate(opener);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Normal, records.WindowState);
        Assert.True(records.IsActive);
    }

    [AvaloniaFact]
    public void A_dialog_takes_focus_over_its_owner_and_hands_it_back_on_close()
    {
        var owner = Show(new Window());
        var dialog = Track(new ConfirmDialog(Message.Of("quit.title"), Message.Of("quit.title"), "quit.confirm"));
        var shown = dialog.ShowBoundedAsync(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialog.IsActive);
        Assert.Same(owner, dialog.Owner); // an owned window stays above its owner

        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(shown.IsCompleted);
        Assert.True(owner.IsActive);
    }
}
