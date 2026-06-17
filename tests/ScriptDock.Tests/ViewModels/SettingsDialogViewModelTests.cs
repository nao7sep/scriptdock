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
    public void AddExtension_NormalisesLeadingDot()
    {
        var vm = new SettingsDialogViewModel(Seed());

        Assert.True(vm.AddExtension("sh"));
        Assert.Contains(".sh", vm.Extensions);
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
    public void Remove_BackToOriginal_ClearsDirty()
    {
        var vm = new SettingsDialogViewModel(Seed());
        vm.AddExtension(".ps1");
        Assert.True(vm.IsDirty);

        vm.RemoveExtension(".ps1");

        Assert.False(vm.IsDirty);
    }
}
