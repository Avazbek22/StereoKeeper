using System.Runtime.InteropServices;

namespace StereoKeeper.App;

/// <summary>
/// Tells the Microsoft Store (MSIX) build apart from the portable exe. The same executable is
/// used for both: only the package Windows started it from differs.
/// </summary>
public static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<string?> CurrentFamilyName = new(ReadFamilyName);

    /// <summary>The package family name, or <c>null</c> for the portable exe.</summary>
    public static string? FamilyName => CurrentFamilyName.Value;

    public static bool IsPackaged => FamilyName is not null;

    /// <summary>
    /// Where a file the app wrote under <c>%AppData%</c> or <c>%LocalAppData%</c> really is. Windows
    /// redirects these writes of a packaged app into the package's own folder, so a path shown to
    /// the user (the log in a startup error, the diagnostics) must point there.
    /// </summary>
    public static string ToRealPath(string path)
        => FamilyName is { } familyName
            ? ToRealPath(
                path,
                familyName,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            : path;

    internal static string ToRealPath(string path, string familyName, string roamingAppData, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(path);

        var cache = Path.Combine(localAppData, "Packages", familyName, "LocalCache");
        if (IsUnder(path, cache))
            return path;

        if (IsUnder(path, roamingAppData))
            return Path.Combine(cache, "Roaming", Path.GetRelativePath(roamingAppData, path));

        if (IsUnder(path, localAppData) && !IsUnder(path, Path.Combine(localAppData, "Packages")))
            return Path.Combine(cache, "Local", Path.GetRelativePath(localAppData, path));

        return path;
    }

    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return false;

        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadFamilyName()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFamilyName(ref length, null);
            if (result == AppModelErrorNoPackage || result != ErrorInsufficientBuffer || length == 0)
                return null;

            // The length includes the terminating null character.
            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 && length > 1
                ? new string(buffer, 0, (int)length - 1)
                : null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
