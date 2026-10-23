using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace ScriptDock.Tests;

/// <summary>
/// The app's version and product name have one source of truth, Directory.Build.props, with copies nothing
/// derives automatically: macOS/Info.plist (its bundle versions and names) and src/ScriptDock/app.manifest
/// (a four-part assemblyIdentity version, always ending .0). These check the copies against it, read as the
/// XML each file is, so a version bump or a rename cannot leave one behind.
/// </summary>
public sealed class VersionConsistencyTests
{
    [Fact]
    public void Info_Plist_And_App_Manifest_Match_The_Directory_Build_Props_Version()
    {
        var version = SourceScan.BuildProperty("Version");
        var plist = SourceScan.PropertyList(SourceScan.RepoFile("macOS", "Info.plist"));
        Assert.Equal(version, plist["CFBundleVersion"].Value);
        Assert.Equal(version, plist["CFBundleShortVersionString"].Value);

        var manifest = XDocument.Load(SourceScan.RepoFile("src", "ScriptDock", "app.manifest"));
        var identity = manifest.Root!.Elements().Single(element => element.Name.LocalName == "assemblyIdentity");
        Assert.Equal(version + ".0", (string?)identity.Attribute("version"));
    }

    // The bundle's names follow the product name the props state; the bundle file itself is named after
    // the assembly.
    [Fact]
    public void Info_Plist_Names_The_Directory_Build_Props_Product()
    {
        var product = SourceScan.BuildProperty("Product");
        var plist = SourceScan.PropertyList(SourceScan.RepoFile("macOS", "Info.plist"));
        Assert.Equal(product, plist["CFBundleName"].Value);
        Assert.Equal(product, plist["CFBundleDisplayName"].Value);
    }
}
