using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ScriptDock.Models;
using ScriptDock.Storage;
using ScriptDock.ViewModels;
using ScriptDock.Views;
using Xunit;

namespace ScriptDock.Tests;

public sealed class ThemeResourcesTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(ThemePreference.System, "Default")]
    [InlineData(ThemePreference.Light, "Light")]
    [InlineData(ThemePreference.Dark, "Dark")]
    public void EachPreferenceMapsToOneThemeVariant(ThemePreference preference, string variant) =>
        Assert.Equal(variant, AppTheme.VariantFor(preference).Key.ToString());

    [Fact]
    public void ANewConfigStartsOnSystemAndWritesTheNameLowercase()
    {
        var config = new AppConfig();
        Assert.Equal(ThemePreference.System, config.Theme);
        config.Theme = ThemePreference.Dark;
        Assert.Contains("\"theme\": \"dark\"", JsonSerializer.Serialize(config, JsonOptions.Default));
    }

    [Theory]
    [InlineData("{}", ThemePreference.System)]
    [InlineData("{\"theme\":\"light\"}", ThemePreference.Light)]
    [InlineData("{\"theme\":\"Dark\"}", ThemePreference.Dark)]
    [InlineData("{\"theme\":\"sepia\"}", ThemePreference.System)]
    [InlineData("{\"theme\":\"2\"}", ThemePreference.System)]
    [InlineData("{\"theme\":2}", ThemePreference.System)]
    [InlineData("{\"theme\":null}", ThemePreference.System)]
    public void AMissingOrUnrecognizedThemeReadsAsSystem(string json, ThemePreference expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<AppConfig>(json, JsonOptions.Default)!.Theme);

    [Fact]
    public void LightAndDarkDefineTheSameThemedBrushes()
    {
        var light = ThemeBrushes("Light");
        var dark = ThemeBrushes("Dark");
        Assert.NotEmpty(light);
        Assert.Equal(light.Keys.OrderBy(key => key), dark.Keys.OrderBy(key => key));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TextKeepsHighContrastInEachTheme(string theme)
    {
        var b = ThemeBrushes(theme);
        var failures = new List<string>();
        void Check(string ink, string surface, double floor)
        {
            var ratio = Contrast(b[ink], b[surface]);
            if (ratio < floor)
                failures.Add($"{ink} on {surface}: {ratio:F2}");
        }

        var chips = new[] { "ChipBackgroundBrush", "ChipHoverBrush", "ChipSelectedBrush", "ChipPressedBrush" };
        foreach (var surface in new[] { "AppBackgroundBrush", "SurfaceBrush", "ContentWellBrush", "SelectionBrush", "SelectionHoverBrush", "ErrorSurfaceBrush" }.Concat(chips))
            Check("TextPrimaryBrush", surface, 4.5);
        foreach (var surface in new[] { "AppBackgroundBrush", "SurfaceBrush", "ContentWellBrush", "ChipBackgroundBrush" })
            Check("TextSecondaryBrush", surface, 4.5);
        foreach (var surface in new[] { "SelectionBrush", "SelectionHoverBrush" })
            Check("SelectionSecondaryBrush", surface, 4.5);
        // A script tile's name by state, and error text wherever it appears.
        foreach (var surface in chips.Append("ContentWellBrush"))
        {
            Check("ScriptHiddenBrush", surface, 4.5);
            Check("DangerTextBrush", surface, 4.5);
        }
        foreach (var surface in new[] { "AppBackgroundBrush", "SurfaceBrush", "ErrorSurfaceBrush" })
            Check("DangerTextBrush", surface, 4.5);
        // The commit button's label, on every fill it takes — resting, hovered and pressed.
        foreach (var fill in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush" })
            Check("AccentForegroundBrush", fill, 4.5);
        // The disabled pair recedes by its own colours rather than by a fade, so its own
        // legibility is the app's to hold and nothing else checks it.
        Check("AccentForegroundDisabledBrush", "AccentDisabledBrush", 4.5);
        Check("InactiveActionForegroundBrush", "InactiveActionBrush", 4.5);
        // The Recent state pill's label, in the window background colour, on each pill fill.
        foreach (var pill in new[] { "RunningBrush", "DangerTextBrush", "TextSecondaryBrush" })
            Check("AppBackgroundBrush", pill, 4.5);
        // Marks: a field's outline, the accent, the scroll-bar thumb, and the tile dots.
        foreach (var surface in new[] { "AppBackgroundBrush", "SurfaceBrush", "ContentWellBrush" })
            foreach (var mark in new[]
                     {
                         "FieldBorderBrush", "AccentBrush",
                         // Every scroll region here floats its bar, which is the brush that one reads.
                         "ScrollBarPanningThumbBackground",
                     })
                Check(mark, surface, 3);
        foreach (var surface in chips.Append("ContentWellBrush"))
            foreach (var dot in new[] { "ScriptNewBrush", "RunningBrush" })
                Check(dot, surface, 3);

        Assert.True(failures.Count == 0, $"{theme}: {string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void WhiteLabelsKeepHighContrastOnTheDangerFill(string theme)
    {
        var b = ThemeBrushes(theme);
        foreach (var fill in new[] { "DangerBrush", "DangerHoverBrush", "DangerPressedBrush" })
            Assert.True(Contrast(Colors.White, b[fill]) >= 4.5, $"{theme}: white on {fill}");
    }

    [Fact]
    public void FluentPalettesRepeatTheThemeAccentRegionAndErrorText()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var palette = AppXaml().Descendants()
                .Single(element => element.Name.LocalName == "ColorPaletteResources" && (string?)element.Attribute(X + "Key") == theme);
            var b = ThemeBrushes(theme);
            Assert.Equal(b["AccentBrush"], Color.Parse((string)palette.Attribute("Accent")!));
            Assert.Equal(b["AppBackgroundBrush"], Color.Parse((string)palette.Attribute("RegionColor")!));
            Assert.Equal(b["DangerTextBrush"], Color.Parse((string)palette.Attribute("ErrorText")!));
        }
    }

    [AvaloniaFact]
    public void CodeBuiltAndMarkupSurfacesRepaintWhenTheThemeChanges()
    {
        var app = Application.Current!;
        var dialog = new ShortcutsDialog(ShortcutCatalog.Build(new Window()));
        try
        {
            AppTheme.Apply(ThemePreference.Light);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var light = CardBackgrounds(dialog);

            AppTheme.Apply(ThemePreference.Dark);
            Dispatcher.UIThread.RunJobs();
            var dark = CardBackgrounds(dialog);

            Assert.Equal(ThemeBrushes("Light")["SurfaceBrush"], Assert.Single(light.Distinct()));
            Assert.Equal(ThemeBrushes("Dark")["SurfaceBrush"], Assert.Single(dark.Distinct()));
            Assert.Equal(ThemeBrushes("Dark")["AppBackgroundBrush"], ((ISolidColorBrush)dialog.Background!).Color);
        }
        finally
        {
            dialog.Close();
            app.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    [AvaloniaFact]
    public void AScriptNameTakesItsStateColourAndARemovedHiddenScriptReadsRemoved()
    {
        var app = Application.Current!;
        var names = new[] { Name(), Name("hidden"), Name("removed"), Name("hidden", "removed") };
        var panel = new StackPanel();
        foreach (var name in names)
            panel.Children.Add(name);
        var host = new Window { Content = panel };
        try
        {
            AppTheme.Apply(ThemePreference.Light);
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var b = ThemeBrushes("Light");
            Assert.Equal(
                new[] { b["TextPrimaryBrush"], b["ScriptHiddenBrush"], b["DangerTextBrush"], b["DangerTextBrush"] },
                names.Select(name => ((ISolidColorBrush)name.Foreground!).Color));
        }
        finally
        {
            host.Close();
            app.RequestedThemeVariant = ThemeVariant.Default;
        }

        static TextBlock Name(params string[] states)
        {
            var block = new TextBlock { Text = "script" };
            block.Classes.Add("scriptName");
            foreach (var state in states)
                block.Classes.Add(state);
            return block;
        }
    }

    [AvaloniaFact]
    public void SettingsOffersTheThreeThemesAsOneRadioGroupInTheDraft()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { Theme = ThemePreference.Dark });
        var view = new SettingsView { DataContext = vm };
        var host = new Window { Content = view, Width = 600, Height = 800 };
        try
        {
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var radios = view.FindControl<StackPanel>("ThemeChoices")!.Children.OfType<RadioButton>().ToList();
            Assert.Equal(new[] { "System", "Light", "Dark" }, radios.Select(radio => (string)radio.Content!));
            Assert.Equal("Dark", radios.Single(radio => radio.IsChecked == true).Content);
            Assert.False(vm.IsDirty);

            radios[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemePreference.Light, vm.Theme);
            Assert.Equal(1, radios.Count(radio => radio.IsChecked == true));
            Assert.True(vm.IsDirty);
        }
        finally
        {
            host.Close();
        }
    }

    [Fact]
    public void RunsExposePillStateNotBrushes()
    {
        var idle = new RecentEntry("/a.command", "a", DateTimeOffset.UnixEpoch, null);
        Assert.False(idle.IsRunning || idle.IsPillFailed);
    }

    private static List<Color> CardBackgrounds(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.CornerRadius == new CornerRadius(8) && border.Background is not null)
            .Select(border => ((ISolidColorBrush)border.Background!).Color)
            .ToList();

    private static XDocument AppXaml() =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "ScriptDock", "App.axaml"));

    private static Dictionary<string, Color> ThemeBrushes(string theme) =>
        AppXaml().Descendants()
            .Single(element => element.Name.LocalName == "ResourceDictionary"
                && (string?)element.Attribute(X + "Key") == theme)
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(X + "Key")!,
                element => Color.Parse((string)element.Attribute("Color")!));

    private static string RepoRoot([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/ScriptDock.Tests/ThemeResourcesTests.cs
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        return Path.GetFullPath(Path.Combine(testsProjectDir, "..", ".."));
    }

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
