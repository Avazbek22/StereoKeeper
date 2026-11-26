using System.Text.Json;
using System.Xml.Linq;
using StereoKeeper.App;
using StereoKeeper.Gui.Services;
using StereoKeeper.Localization;
using Xunit;

namespace StereoKeeper.Tests;

/// <summary>
/// The Microsoft Store manifest in packaging/store must stay in step with the app: a mismatch
/// only shows up after an upload to Partner Center or on a user's PC.
/// </summary>
public sealed class StorePackageTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static readonly string StoreRoot = Path.Combine(FindRepositoryRoot(), "packaging", "store");
    private static readonly XDocument Manifest = XDocument.Load(Path.Combine(StoreRoot, "Package.appxmanifest"));

    [Fact]
    public void EveryInterfaceLanguageIsAStoreLanguage()
    {
        var declared = Manifest.Root!.Element(Foundation + "Resources")!
            .Elements(Foundation + "Resource")
            .Select(resource => (string)resource.Attribute("Language")!)
            .ToList();

        var expected = AppLanguageCatalog.All.Select(language => ToStoreLanguage(language.Code)).ToList();

        Assert.Equal("en-us", declared[0]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheStoreTextsCoverEveryLanguage()
    {
        using var locales = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "winget-locales.json")));
        var languages = locales.RootElement.EnumerateObject().Select(locale => ToStoreLanguage(locale.Name)).ToList();

        Assert.Equal(
            AppLanguageCatalog.All.Select(language => ToStoreLanguage(language.Code)).Order(StringComparer.Ordinal),
            languages.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheStartupTaskStartsTheAppInTheTray()
    {
        var extension = Manifest.Descendants(Desktop + "Extension")
            .Single(element => (string?)element.Attribute("Category") == "windows.startupTask");
        var task = extension.Element(Desktop + "StartupTask")!;

        Assert.Equal(StartupTaskAutostart.TaskId, (string?)task.Attribute("TaskId"));
        Assert.Equal("false", (string?)task.Attribute("Enabled"));
        Assert.Equal(LaunchOptions.TrayArgument, (string?)extension.Attribute(Uap10 + "Parameters"));
        Assert.Equal(AppInfo.AppName + ".exe", (string?)extension.Attribute("Executable"));
    }

    [Fact]
    public void ThePackageRunsTheAppExeWithFullTrustOnly()
    {
        var application = Manifest.Descendants(Foundation + "Application").Single();
        var capabilities = Manifest.Root!.Element(Foundation + "Capabilities")!.Elements().ToList();

        Assert.Equal(AppInfo.AppName + ".exe", (string?)application.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)application.Attribute("EntryPoint"));
        Assert.Equal(["runFullTrust"], capabilities.Select(capability => (string)capability.Attribute("Name")!));
        Assert.All(capabilities, capability => Assert.Equal(Rescap + "Capability", capability.Name));
    }

    [Fact]
    public void EveryLogoTheManifestNamesExists()
    {
        var visual = Manifest.Descendants(Uap + "VisualElements").Single();
        var logos = new[]
        {
            (string)Manifest.Root!.Element(Foundation + "Properties")!.Element(Foundation + "Logo")!,
            (string)visual.Attribute("Square150x150Logo")!,
            (string)visual.Attribute("Square44x44Logo")!
        };

        foreach (var logo in logos)
        {
            var name = Path.GetFileNameWithoutExtension(logo);
            var files = Directory.GetFiles(Path.Combine(StoreRoot, "Assets"), name + ".scale-*.png");
            Assert.Equal(5, files.Length);
        }

        Assert.Equal(15 * 3, Directory.GetFiles(Path.Combine(StoreRoot, "Assets"), "Square44x44Logo.targetsize-*.png").Length);
    }

    /// <summary>Partner Center has no es-419; Latin American Spanish is listed as es-mx.</summary>
    private static string ToStoreLanguage(string code)
        => string.Equals(code, "es-419", StringComparison.OrdinalIgnoreCase) ? "es-mx" : code.ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StereoKeeper.sln")))
            directory = directory.Parent;

        return directory?.FullName
               ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
