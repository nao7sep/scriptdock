using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using ScriptDock.Models;
using ScriptDock.Tests.Fakes;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests.Views;

/// <summary>
/// Every text box the app shows is single-line and unwrapped. A wrapping Avalonia editor takes seconds per
/// edit on one long pasted line, so a text box that ever needs wrapping must turn it off while any line
/// exceeds about 10,000 graphemes (text-cleanup conventions, rule 4). This fails first, so that rule is
/// added with it.
/// </summary>
public sealed class TextBoxWrappingTests : WindowTest
{
    [AvaloniaFact]
    public void No_text_box_wraps()
    {
        var state = new FakeJsonStore<AppState>();
        var windows = new Window[]
        {
            Show(new MainWindow()),
            Show(new RecordsWindow { DataContext = new RecordsWindowViewModel(new FakeRecordReader(), state, state.Value, new FakeTimeProvider()) }),
            Show(new SettingsDialog(new SettingsDialogViewModel(new AppConfig()), _ => Task.FromResult(true))),
        };

        var boxes = windows.SelectMany(window => window.GetVisualDescendants().OfType<TextBox>()).ToArray();

        // The console input, Records search, UI font, extension and ignore-pattern entries.
        Assert.True(boxes.Length >= 5, $"only {boxes.Length} text boxes were found");
        Assert.All(boxes, box => Assert.Equal(TextWrapping.NoWrap, box.TextWrapping));
    }
}
