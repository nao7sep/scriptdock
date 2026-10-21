using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScriptDock.I18n;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using System.Linq;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class SettingsDialogViewModelTests
{
    private static AppConfig Seed() => new()
    {
        RootDirs = { "/code" },
        Extensions = [".command"],
        IgnorePatterns = [new("node_modules/")],
    };

    [Fact]
    public void ResetAbsentBuiltIns_DoesNotDirtyTheDraft()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        vm.ResetExtensions();
        vm.ResetIgnorePatterns();
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void ResetThenEdit_KeepsTheEditedWholeSet()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { Extensions = [".sh"] });
        vm.ResetExtensions();
        Assert.True(vm.IsDirty);
        vm.AddExtension(".custom");
        Assert.True(vm.IsDirty);
        Assert.Equal([ConfigDefaults.DefaultExtension, ".custom"], vm.ToConfig().Extensions);
    }

    [Fact]
    public void IsDirty_ComparesTheCleanedDraft()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { UiFontFamily = "Inter" });
        vm.UiFontFamily = "  Inter\n";
        Assert.False(vm.IsDirty);
        Assert.Equal("Inter", vm.ToConfig().UiFontFamily);
    }

    [Fact]
    public void New_IsNotDirty()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void UiFont_SeedsFromConfig_AndDirtiesOnChange()
    {
        var config = Seed();
        config.UiFontFamily = "Inter";
        var vm = new SettingsDialogViewModel(config);

        Assert.Equal("Inter", vm.UiFontFamily);
        Assert.False(vm.IsDirty);

        vm.UiFontFamily = "Iosevka, monospace";
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddPickedRootDir_PreservesLegalLeadingAndTrailingSpaces()
    {
        var vm = new SettingsDialogViewModel(Seed());
        var picked = Path.Combine(Path.GetTempPath(), " scriptdock-picked ");

        Assert.True(await vm.AddPickedRootDirAsync(picked));
        Assert.Contains(Path.GetFullPath(picked), vm.RootDirs);
        Assert.True(vm.IsDirty);
        Assert.False(await vm.AddPickedRootDirAsync(picked));
    }

    [AvaloniaFact]
    public async Task HeldPickedIdentityKeepsTheUiAvailableAndRechecksAChangedDraft()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var path = Path.Combine(Path.GetTempPath(), "scriptdock-picked-" + Guid.NewGuid().ToString("N"));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var pick = vm.AddPickedRootDirAsync(path, value =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            if (Interlocked.Increment(ref calls) == 1) { held.Set(); release.Wait(); }
            return value;
        });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(pick.IsCompleted);
            vm.RootDirs.Add(path); // UI work can settle while the physical identity probe is held.
            vm.RootDirs.CollectionChanged += (_, _) => Assert.True(Dispatcher.UIThread.CheckAccess());
        }
        finally { release.Set(); await pick; }
        Assert.False(await pick);
        Assert.Single(vm.RootDirs);
        Assert.True(calls >= 2);
    }

    [Fact]
    public void RootPickerFailure_IsOwnedByTheRootSectionWithoutDiagnostics()
    {
        const string hostile = "EACCES Error invoking remote method IPC /private/tmp/hostile-sentinel";
        var vm = new SettingsDialogViewModel(Seed());

        vm.ReportRootPickerFailure(new IOException(hostile));

        Assert.True(vm.HasRootPickerResult);
        Assert.DoesNotContain(hostile, vm.RootPickerResult, StringComparison.Ordinal);

        vm.ResolveRootPickerFailure();
        Assert.False(vm.HasRootPickerResult);
    }

    [Fact]
    public void AddExtension_NormalisesLeadingDot()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.True(vm.AddExtension("sh"));
        Assert.Contains(".sh", vm.Extensions.Select(row => row.Text));
    }

    [Fact]
    public void AddExtension_DedupsCaseInsensitively_MatchingTheScanner()
    {
        var vm = new SettingsDialogViewModel(Seed()); // seeded with ".command"

        // The scanner matches extensions case-insensitively, so a differently-cased
        // duplicate must not create a second entry.
        Assert.False(vm.AddExtension(".COMMAND"));
        Assert.False(vm.AddExtension("Command")); // normalises to ".Command", still a dup
        Assert.Single(vm.Extensions);
    }

    [Fact]
    public void AddExtension_RejectsWhitespace_AndReportsIt()
    {
        var vm = new SettingsDialogViewModel(Seed());

        // Interior space — a pasted multi-token blob, never a real extension.
        Assert.False(vm.AddExtension(".com mand"));
        Assert.NotEqual(string.Empty, vm.ExtensionError);
        Assert.DoesNotContain(".com mand", vm.Extensions.Select(row => row.Text));

        // A multi-line paste leaks a newline into the single-line field.
        Assert.False(vm.AddExtension(".sh\n.ps1"));

        // A clean value still adds and clears the error.
        Assert.True(vm.AddExtension(".sh"));
        Assert.Contains(".sh", vm.Extensions.Select(row => row.Text));
        Assert.Equal(string.Empty, vm.ExtensionError);
        Assert.False(vm.HasExtensionError);
        Assert.Equal(string.Empty, vm.ExtensionItemStatus);
    }

    [Fact]
    public void AddExtension_BlankAndDuplicateExposeInvalidState()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.False(vm.AddExtension("  "));
        Assert.True(vm.HasExtensionError);
        Assert.Equal("Invalid", vm.ExtensionItemStatus);

        Assert.False(vm.AddExtension(".COMMAND"));
        Assert.Contains("already", vm.ExtensionError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pattern_row_is_checked_on_Save_by_its_kind()
    {
        var vm = new SettingsDialogViewModel(Seed());
        var multiline = vm.AddIgnorePattern();
        multiline.Text = "node_modules/\nobj/";
        var regex = vm.AddIgnorePattern();
        regex.Text = "[";
        regex.Kind = IgnorePatternKind.Regex;
        var wildcard = vm.AddIgnorePattern();
        wildcard.Text = "[";
        var spaced = vm.AddIgnorePattern();
        spaced.Text = "My Projects/";

        Assert.False(vm.Validate());
        // A multi-line paste is rejected rather than flattened, which would change what it matches.
        Assert.True(multiline.HasError);
        Assert.Contains("[", regex.Error);
        Assert.Contains(Localizer.T("settings.patternInvalidRegex"), regex.Error);
        // A bracket is itself in a wildcard, and an interior space is part of a path.
        Assert.False(wildcard.HasError);
        Assert.False(spaced.HasError);
        Assert.Equal(SettingsTab.IgnorePatterns, vm.Tab);

        regex.Text = "\\.bak$";
        Assert.False(regex.HasError); // editing clears the mark until the next Save
        multiline.Text = "obj/";
        Assert.True(vm.Validate());
    }

    [Fact]
    public void A_failing_row_on_the_other_tab_brings_that_tab_forward()
    {
        var vm = new SettingsDialogViewModel(Seed()) { Tab = SettingsTab.IgnorePatterns };
        vm.Extensions[0].Text = ".com mand";

        Assert.False(vm.Validate());
        Assert.Equal(SettingsTab.General, vm.Tab);
    }

    [Fact]
    public void Add_appends_a_blank_wildcard_row_that_Save_drops()
    {
        var vm = new SettingsDialogViewModel(Seed());

        var row = vm.AddIgnorePattern();

        Assert.Equal((string.Empty, IgnorePatternKind.Wildcard, null), (row.Text, row.Kind, row.Id));
        Assert.Same(row, vm.IgnorePatterns[^1]);
        Assert.True(vm.Validate());
        Assert.Equal([new IgnorePattern("node_modules/")], vm.ToConfig().IgnorePatterns);
        Assert.False(vm.IsDirty);
    }

    // A built-in keeps its id and note while the row holds it; edited away it is the user's own, and edited
    // back, or typed anew, it is that built-in again.
    [Fact]
    public void A_built_in_row_keeps_its_note_while_it_holds_that_built_in()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var git = vm.IgnorePatterns[0];
        Assert.Equal(("dev.git", Localizer.T("ignoreNote.git")), (git.Id, git.Note));
        Assert.True(git.HasNote);

        git.Text = ".hg/";
        Assert.Null(git.Id);
        Assert.False(git.HasNote);
        git.Text = " .GIT/ ";
        Assert.Equal("dev.git", git.Id);

        var typed = vm.AddIgnorePattern();
        typed.Text = "/bin/(Debug|Release)/";
        Assert.Null(typed.Id); // the built-in is a regular expression
        typed.Kind = IgnorePatternKind.Regex;
        Assert.Equal("dotnet.bin", typed.Id);
    }

    [Fact]
    public void Reset_restores_exactly_the_built_ins()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { IgnorePatterns = [new("*.tmp"), new("/old/", IgnorePatternKind.Regex)] });

        vm.ResetIgnorePatterns();

        Assert.Equal(ConfigDefaults.BuiltInIgnorePatterns, vm.ToConfig().IgnorePatterns);
        Assert.True(vm.IsDirty);
        Assert.Contains("ignorePatterns", vm.ChosenSets);
    }

    [Fact]
    public void Remove_BackToOriginal_ClearsDirty()
    {
        var vm = new SettingsDialogViewModel(Seed());
        vm.AddExtension(".ps1");
        Assert.True(vm.IsDirty);

        vm.RemoveExtension(vm.Extensions.Single(row => row.Text == ".ps1"));

        Assert.False(vm.IsDirty);
    }

    // ── Rows typed in place (config-sets-conventions, In the interface) ─────────────────────────

    [Fact]
    public void Each_row_edits_in_place_and_Save_trims_drops_blanks_and_merges_case()
    {
        var vm = new SettingsDialogViewModel(new AppConfig { Extensions = [".command"], IgnorePatterns = [new("a/")] });
        vm.Extensions[0].Text = "  sh  ";
        vm.Extensions.Add(new EditableItem("   "));
        vm.Extensions.Add(new EditableItem(".SH"));
        vm.IgnorePatterns[0].Text = " b/ ";
        vm.AddIgnorePattern().Text = "B/";
        vm.AddIgnorePattern().Text = "  ";
        var regex = vm.AddIgnorePattern();
        regex.Text = "b/";
        regex.Kind = IgnorePatternKind.Regex; // the same text read another way is another pattern

        Assert.True(vm.Validate());
        Assert.Equal([".sh"], vm.ToConfig().Extensions);
        Assert.Equal([new IgnorePattern("b/"), new IgnorePattern("b/", IgnorePatternKind.Regex)], vm.ToConfig().IgnorePatterns);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void A_row_that_fails_its_check_is_marked_with_a_message_naming_it_and_holds_Save()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var bad = vm.AddIgnorePattern();
        bad.Text = "[";
        bad.Kind = IgnorePatternKind.Regex;
        vm.Extensions[0].Text = ".com mand";

        Assert.False(vm.Validate());
        Assert.True(bad.HasError);
        Assert.Contains("[", bad.Error);
        Assert.True(vm.Extensions[0].HasError);
        Assert.Contains(".com mand", vm.Extensions[0].Error);
        Assert.False(vm.IgnorePatterns[0].HasError); // the built-ins pass

        bad.Text = "/fixed/"; // editing clears the mark until the next check
        Assert.False(bad.HasError);
    }

    [Fact]
    public void An_empty_extension_list_holds_Save()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        vm.Extensions[0].Text = " ";

        Assert.False(vm.Validate());
        Assert.True(vm.HasExtensionError);
    }

    [Fact]
    public void Reordering_a_list_is_no_change()
    {
        var vm = new SettingsDialogViewModel(new AppConfig());
        var first = vm.IgnorePatterns[0];
        vm.IgnorePatterns.RemoveAt(0);
        vm.IgnorePatterns.Add(first);

        Assert.False(vm.IsDirty);
    }
}
