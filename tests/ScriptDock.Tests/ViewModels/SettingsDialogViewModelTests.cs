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
        Extensions = { ".command" },
        IgnorePatterns = { "/node_modules/" },
    };

    [Fact]
    public void New_IsNotDirty()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AddRootDir_TrimsDedupsAndDirties()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.True(vm.AddRootDir("  /more  "));
        Assert.Contains("/more", vm.RootDirs);
        Assert.True(vm.IsDirty);

        Assert.False(vm.AddRootDir("/more"));   // duplicate
        Assert.False(vm.AddRootDir("   "));     // empty
    }

    [Fact]
    public void AddRootDir_ResolvesToAbsoluteUnderHome_NotWorkingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var vm = new SettingsDialogViewModel(Seed());

        // A relative entry anchors to the home directory, never the cwd.
        Assert.True(vm.AddRootDir("somedir"));
        var expected = Path.GetFullPath(Path.Combine(home, "somedir"));
        Assert.Contains(expected, vm.RootDirs);
        Assert.DoesNotContain("somedir", vm.RootDirs);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "somedir")),
            expected);

        // A ~/x entry expands to <home>/x.
        Assert.True(vm.AddRootDir("~/x"));
        Assert.Contains(Path.GetFullPath(Path.Combine(home, "x")), vm.RootDirs);

        // An absolute entry is kept as-is.
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "scriptdock-root-abs"));
        Assert.True(vm.AddRootDir(absolute));
        Assert.Contains(absolute, vm.RootDirs);
    }

    [Fact]
    public void AddRootDir_RejectsDuplicateAfterResolution()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.True(vm.AddRootDir("somedir"));
        // The same logical root expressed differently resolves to the same absolute
        // path and is therefore rejected as a duplicate.
        Assert.False(vm.AddRootDir("  somedir  "));
        Assert.False(vm.AddRootDir("~/somedir"));
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
    }

    [Fact]
    public void ProcessSettings_SeedFromConfig_AndDirtyOnToggle()
    {
        var config = Seed();
        config.KillProcessesOnClose = false;
        config.RecaptureProcessesOnLaunch = true;
        var vm = new SettingsDialogViewModel(config);

        // Seeded from config; an unchanged draft is not dirty.
        Assert.False(vm.KillProcessesOnClose);
        Assert.True(vm.RecaptureProcessesOnLaunch);
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
