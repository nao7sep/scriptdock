using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// A dialog whose Save is still running is busy (modal-dialog-conventions): input typed meanwhile cannot
/// be lost by the close that follows, a second Save cannot overlap the first, and the user's own close
/// cannot leave behind a save that still lands.
/// </summary>
public sealed class SettingsDialogBusyTests : WindowTest
{
    private (SettingsDialog Dialog, SettingsDialogViewModel Draft) Open(TaskCompletionSource<bool> save, Counter saves)
    {
        var draft = new SettingsDialogViewModel(new AppConfig());
        draft.Extensions.Add(".sh");
        var dialog = Show(new SettingsDialog(draft, _ =>
        {
            saves.Count++;
            return save.Task;
        }));
        return (dialog, draft);
    }

    private static Button ButtonOf(Window dialog, string tag) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, tag));

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void WhileSaving_TheDialogIsLockedAndStaysOpen_ThenClosesWhenTheSaveLands()
    {
        var save = new TaskCompletionSource<bool>();
        var saves = new Counter();
        var (dialog, _) = Open(save, saves);
        var saveButton = ButtonOf(dialog, "save");

        Click(saveButton);
        Assert.False(saveButton.IsEffectivelyEnabled);
        Assert.False(ButtonOf(dialog, "cancel").IsEffectivelyEnabled);
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.IsEffectivelyEnabled);

        Click(saveButton);
        dialog.Close(); // Escape and the close button both close this way
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, saves.Count);
        Assert.True(dialog.IsVisible);

        save.SetResult(true);
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.IsVisible);
        Assert.True(dialog.Saved);
    }

    [AvaloniaFact]
    public void AFailedSave_UnlocksTheDialogWithTheDraftAndItsMessage()
    {
        var save = new TaskCompletionSource<bool>();
        var saves = new Counter();
        var (dialog, draft) = Open(save, saves);
        var saveButton = ButtonOf(dialog, "save");

        Click(saveButton);
        save.SetResult(false);
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialog.IsVisible);
        Assert.True(saveButton.IsEffectivelyEnabled);
        Assert.Contains(".sh", draft.Extensions);
        Assert.Equal("settings.saveFailed", draft.SaveErrorMessage?.Key);
    }

    private sealed class Counter
    {
        public int Count;
    }
}
