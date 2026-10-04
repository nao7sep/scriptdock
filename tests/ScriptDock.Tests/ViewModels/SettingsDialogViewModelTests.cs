using System;
using System.IO;
using ScriptDock.Models;
using ScriptDock.ViewModels;
using Xunit;

namespace ScriptDock.Tests.ViewModels;

public sealed class SettingsDialogViewModelTests
{
    private static AppConfig Seed() => new()
    {
        RootDirs = { "/code" },
        Extensions = [".command"],
        IgnorePatterns = ["/node_modules/"],
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
    public void AddPickedRootDir_PreservesLegalLeadingAndTrailingSpaces()
    {
        var vm = new SettingsDialogViewModel(Seed());
        var picked = Path.Combine(Path.GetTempPath(), " scriptdock-picked ");

        Assert.True(vm.AddPickedRootDir(picked));
        Assert.Contains(Path.GetFullPath(picked), vm.RootDirs);
        Assert.True(vm.IsDirty);
        Assert.False(vm.AddPickedRootDir(picked));
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
        Assert.Contains(".sh", vm.Extensions);
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
        Assert.DoesNotContain(".com mand", vm.Extensions);

        // A multi-line paste leaks a newline into the single-line field.
        Assert.False(vm.AddExtension(".sh\n.ps1"));

        // A clean value still adds and clears the error.
        Assert.True(vm.AddExtension(".sh"));
        Assert.Contains(".sh", vm.Extensions);
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
    public void AddIgnorePattern_RejectsLineBreak_ButKeepsInteriorSpaces()
    {
        var vm = new SettingsDialogViewModel(Seed());

        // A multi-line paste — reject rather than flatten (flattening a newline to a space would
        // silently change what the regex matches).
        Assert.False(vm.AddIgnorePattern("/node_modules/\n/obj/"));
        Assert.NotEqual(string.Empty, vm.PatternError);

        // An interior space is legitimate regex content — a path can contain one — so it is kept.
        Assert.True(vm.AddIgnorePattern("/My Projects/"));
        Assert.Contains("/My Projects/", vm.IgnorePatterns);
        Assert.Equal(string.Empty, vm.PatternError);
    }

    [Fact]
    public void AddIgnorePattern_RejectsInvalidRegex_AndReportsIt()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.False(vm.AddIgnorePattern("["));
        Assert.NotEqual(string.Empty, vm.PatternError);
        Assert.DoesNotContain("[", vm.IgnorePatterns);

        Assert.True(vm.AddIgnorePattern("/obj/"));
        Assert.Equal(string.Empty, vm.PatternError);
        Assert.False(vm.HasPatternError);
        Assert.Equal(string.Empty, vm.PatternItemStatus);
    }

    [Fact]
    public void AddIgnorePattern_BlankAndDuplicateExposeInvalidState()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.False(vm.AddIgnorePattern("  "));
        Assert.True(vm.HasPatternError);
        Assert.Equal("Invalid", vm.PatternItemStatus);

        Assert.False(vm.AddIgnorePattern("/NODE_MODULES/"));
        Assert.Contains("already", vm.PatternError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessSettings_SeedFromConfig_AndDirtyOnToggle()
    {
        var config = Seed();
        config.KillProcessesOnClose = false;
        var vm = new SettingsDialogViewModel(config);

        // Seeded from config; an unchanged draft is not dirty.
        Assert.False(vm.KillProcessesOnClose);
        Assert.False(vm.IsDirty);

        // Toggling a process setting dirties the draft (so Save enables).
        vm.KillProcessesOnClose = true;
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Remove_BackToOriginal_ClearsDirty()
    {
        var vm = new SettingsDialogViewModel(Seed());
        vm.AddExtension(".ps1");
        Assert.True(vm.IsDirty);

        vm.RemoveExtension(".ps1");

        Assert.False(vm.IsDirty);
    }
}
