using System.Text.Json;
using StereoKeeper.Gui.ViewModels;
using StereoKeeper.Localization;
using Xunit;

namespace StereoKeeper.Tests.Gui;

/// <summary>
/// Windows deletes the settings and the log of the Store build on uninstall, so its help must not
/// send the user to %AppData% to delete them; the portable exe keeps the original text.
/// </summary>
public sealed class StoreHelpTextTests
{
    [Fact]
    public void TheStoreBuildExplainsThatWindowsCleansUp()
    {
        var details = new UiText(isStoreApp: true).HelpSections[^1];

        Assert.True(details.IsAside);
        Assert.Equal(Text.Get("gui.help.details.storeBody"), details.Body);
    }

    [Fact]
    public void ThePortableExeNamesTheFoldersToDelete()
    {
        var details = new UiText(isStoreApp: false).HelpSections[^1];

        Assert.Equal(Text.Get("gui.help.details.body"), details.Body);
        Assert.Contains(@"%AppData%\StereoKeeper", details.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBuildsShowTheSameHelpCards()
        => Assert.Equal(
            new UiText(isStoreApp: false).HelpSections.Select(section => section.Title),
            new UiText(isStoreApp: true).HelpSections.Select(section => section.Title));

    [Fact]
    public void EveryLanguageTellsTheSameStoryInBothBuilds()
    {
        foreach (var language in AppLanguageCatalog.All)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Localization", "Resources", $"{language.ResourceCode}.json");
            if (!File.Exists(path))
                path = Path.Combine(FindRepositoryRoot(), "StereoKeeper", "Localization", "Resources", $"{language.ResourceCode}.json");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var portable = document.RootElement.GetProperty("gui.help.details.body").GetString()!;
            var store = document.RootElement.GetProperty("gui.help.details.storeBody").GetString()!;

            // The two texts share everything up to the service names; only the uninstall part differs.
            var shared = portable.IndexOf("BthHFSrv", StringComparison.Ordinal) + "BthHFSrv".Length;
            Assert.True(shared > "BthHFSrv".Length, $"{language.Code}: the services are not named.");
            Assert.Equal(portable[..shared], store[..shared]);
            Assert.True(store.Length > shared + 10, $"{language.Code}: the Store text says nothing about uninstalling.");
            Assert.DoesNotContain("%AppData%", store, StringComparison.Ordinal);
            Assert.DoesNotContain("%LocalAppData%", store, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StereoKeeper.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
