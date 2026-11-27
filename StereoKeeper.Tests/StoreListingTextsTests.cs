using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace StereoKeeper.Tests;

/// <summary>
/// The Store listing texts in packaging/store/texts, one file per Store language. Partner Center
/// rejects an import that breaks its limits only after the upload, and a translation that drops a
/// fact (a number, a product name) would mislead users in that market, so both are checked here.
/// Publish-Store.ps1 checks the same limits before it writes the import file.
/// </summary>
public sealed partial class StoreListingTextsTests
{
    private const int ShortDescriptionLimit = 1000;
    private const int DescriptionLimit = 10000;
    private const int FeatureCount = 20;
    private const int FeatureLimit = 200;
    private const int SearchTermCount = 7;
    private const int SearchTermLimit = 30;
    private const int SearchTermWords = 21;
    private const int CaptionLimit = 200;

    private static readonly string[] Keys = ["shortDescription", "description", "features", "searchTerms", "screenshotCaptions"];

    // Facts every language must keep: the product, the systems, the numbers, and the games and apps
    // people search the problem with.
    private static readonly string[] Facts =
    [
        "StereoKeeper", "Windows 11", "Windows 10", "2004", "25", "30", "MIT", "GitHub", "A2DP", "HFP",
        "Call of Duty", "Warzone", "OBS Studio", "Discord",
    ];

    private static readonly string StoreRoot = Path.Combine(FindRepositoryRoot(), "packaging", "store");
    private static readonly string TextsDirectory = Path.Combine(StoreRoot, "texts");

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var language in StoreLanguages())
            data.Add(language);

        return data;
    }

    private static List<string> StoreLanguages()
    {
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var manifest = XDocument.Load(Path.Combine(StoreRoot, "Package.appxmanifest"));
        return manifest.Root!.Element(foundation + "Resources")!.Elements(foundation + "Resource")
            .Select(resource => (string)resource.Attribute("Language")!)
            .ToList();
    }

    [Fact]
    public void EveryTextFileBelongsToAStoreLanguage()
    {
        var languages = StoreLanguages().Order(StringComparer.Ordinal);
        var files = Directory.GetFiles(TextsDirectory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal);

        Assert.Equal(languages, files);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheFileIsPlainUtf8WithTheExpectedFields(string language)
    {
        var bytes = File.ReadAllBytes(PathOf(language));

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The file starts with a byte order mark.");
        Assert.DoesNotContain((byte)'\r', bytes);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(Keys, document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheDescriptionsFitPartnerCenter(string language)
    {
        var texts = Read(language);

        AssertText(texts.ShortDescription, ShortDescriptionLimit);
        AssertText(texts.Description, DescriptionLimit);
        Assert.DoesNotContain("\n\n\n", texts.Description);
        Assert.DoesNotContain("\t", texts.Description);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheDescriptionKeepsTheParagraphsOfTheEnglishOne(string language)
    {
        static int Paragraphs(string description) => description.Split("\n\n").Length;

        Assert.Equal(Paragraphs(Read("en-us").Description), Paragraphs(Read(language).Description));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheDescriptionKeepsEveryFact(string language)
    {
        var description = Read(language).Description;

        Assert.All(Facts, fact => Assert.Contains(fact, description, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheFeaturesFillEverySlot(string language)
    {
        var features = Read(language).Features;

        Assert.Equal(FeatureCount, features.Count);
        Assert.All(features, feature =>
        {
            AssertText(feature, FeatureLimit);
            Assert.DoesNotContain("\n", feature);
        });
        Assert.Equal(features.Count, features.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheSearchTermsFitPartnerCenter(string language)
    {
        var terms = Read(language).SearchTerms;

        Assert.InRange(terms.Count, 1, SearchTermCount);
        Assert.All(terms, term => AssertText(term, SearchTermLimit));
        Assert.Equal(terms.Count, terms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Hyphenated parts count as words, to stay within the limit however Partner Center counts.
        Assert.InRange(terms.Sum(term => WordSeparator().Split(term).Count(word => word.Length > 0)), 1, SearchTermWords);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheSearchTermsNameNoOtherProduct(string language)
    {
        // Store policy 10.1.3: search terms must not use the names of other apps, games or brands.
        // The description may name them; the search terms may not.
        Assert.All(Read(language).SearchTerms, term => Assert.DoesNotMatch(OtherProduct(), term));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryScreenshotHasACaption(string language)
    {
        var captions = Read(language).ScreenshotCaptions;
        var screenshots = Directory.GetFiles(Path.Combine(StoreRoot, "listing", "Screenshots", language), "*.png");

        Assert.Equal(screenshots.Length, captions.Count);
        Assert.All(captions, caption => AssertText(caption, CaptionLimit));
    }

    private static void AssertText(string text, int limit)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), "A text is empty.");
        Assert.Equal(text.Trim(), text);
        Assert.True(text.Length <= limit, $"{text.Length} characters, the limit is {limit}: {text}");
    }

    private static string PathOf(string language) => Path.Combine(TextsDirectory, $"{language}.json");

    private static ListingTexts Read(string language)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(PathOf(language)));
        var root = document.RootElement;

        static List<string> List(JsonElement element) => element.EnumerateArray().Select(item => item.GetString()!).ToList();

        return new ListingTexts(
            root.GetProperty("shortDescription").GetString()!,
            root.GetProperty("description").GetString()!,
            List(root.GetProperty("features")),
            List(root.GetProperty("searchTerms")),
            List(root.GetProperty("screenshotCaptions")));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StereoKeeper.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex WordSeparator();

    [GeneratedRegex(@"\b(call of duty|cod|warzone|black ops|modern warfare|activision|helldivers|counter-strike|cs2|roblox|discord|obs|teams|zoom|google meet|airpods|apple|sony|bose|galaxy|samsung|jbl|soundcore|jabra|sennheiser|beats)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OtherProduct();

    private sealed record ListingTexts(
        string ShortDescription,
        string Description,
        List<string> Features,
        List<string> SearchTerms,
        List<string> ScreenshotCaptions);
}
