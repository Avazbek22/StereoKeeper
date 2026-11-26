using StereoKeeper.App;
using Xunit;

namespace StereoKeeper.Tests;

public sealed class PackageIdentityTests
{
    private const string FamilyName = "Publisher.StereoKeeper_84v5br12cncq6";
    private const string Roaming = @"C:\Users\gamer\AppData\Roaming";
    private const string Local = @"C:\Users\gamer\AppData\Local";
    private const string Cache = @"C:\Users\gamer\AppData\Local\Packages\Publisher.StereoKeeper_84v5br12cncq6\LocalCache";

    [Fact]
    public void TheTestRunIsNotPackaged()
    {
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FamilyName);
        Assert.Equal(@"C:\Users\gamer\AppData\Local\StereoKeeper\logs\app.log",
            PackageIdentity.ToRealPath(@"C:\Users\gamer\AppData\Local\StereoKeeper\logs\app.log"));
    }

    [Theory]
    [InlineData(@"C:\Users\gamer\AppData\Local\StereoKeeper\logs\app.log", Cache + @"\Local\StereoKeeper\logs\app.log")]
    [InlineData(@"C:\Users\gamer\AppData\Roaming\StereoKeeper\gui-settings.json", Cache + @"\Roaming\StereoKeeper\gui-settings.json")]
    [InlineData(@"c:\users\GAMER\appdata\roaming\StereoKeeper\language.json", Cache + @"\Roaming\StereoKeeper\language.json")]
    public void PackagedWritesUnderAppDataPointIntoThePackageCache(string path, string expected)
        => Assert.Equal(expected, PackageIdentity.ToRealPath(path, FamilyName, Roaming, Local));

    [Theory]
    [InlineData(@"C:\ProgramData\BluetoothHandsFreeToggle\backup.json")]
    [InlineData(Cache + @"\Local\StereoKeeper\logs\app.log")]
    [InlineData(@"C:\Users\gamer\AppData\Local\Packages\Other.App_1234\LocalState\x.txt")]
    [InlineData(@"C:\Users\gamer\AppData\LocalLow\StereoKeeper\x.txt")]
    [InlineData(@"D:\Games\app.log")]
    public void OtherPathsAreNotRedirected(string path)
        => Assert.Equal(path, PackageIdentity.ToRealPath(path, FamilyName, Roaming, Local));
}
