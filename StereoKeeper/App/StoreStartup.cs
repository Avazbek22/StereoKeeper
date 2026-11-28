using System.Runtime.InteropServices;
using StereoKeeper.Windows;

namespace StereoKeeper.App;

/// <summary>
/// At sign-in the Store build must wait in the tray like the portable exe does. The manifest
/// passes <see cref="LaunchOptions.TrayArgument"/> to the startup task; Windows does not document
/// that it always does, so the activation kind is checked as well.
/// </summary>
internal static class StoreStartup
{
    public static LaunchOptions Resolve(LaunchOptions options)
        => Resolve(options, PackageIdentity.IsPackaged, StartupTaskInterop.WasStartedByStartupTask);

    internal static LaunchOptions Resolve(LaunchOptions options, bool isPackaged, Func<bool> wasStartedByStartupTask)
    {
        if (options.StartHidden || !isPackaged)
            return options;

        try
        {
            return wasStartedByStartupTask() ? options with { StartHidden = true } : options;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException
                                              or UnauthorizedAccessException or InvalidOperationException)
        {
            // Showing the panel at sign-in is the lesser evil than not starting at all.
            CrashLog.Write("Reading how Windows started the app failed", exception);
            return options;
        }
    }
}
