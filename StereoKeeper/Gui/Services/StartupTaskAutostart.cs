using System.Runtime.InteropServices;
using StereoKeeper.App;
using StereoKeeper.Windows;

namespace StereoKeeper.Gui.Services;

/// <summary>
/// Start with Windows for the Microsoft Store build. Windows keeps the registry writes of a
/// packaged app private to its package, so the Run key would never be seen at sign-in; the
/// startup task declared in the package manifest is switched instead.
/// </summary>
/// <remarks>
/// The user can also turn the task off in Task Manager or in Settings → Apps → Startup. After
/// that Windows no longer lets the app turn it back on by itself, so the Startup page is opened
/// for the user to do it there.
/// </remarks>
public sealed class StartupTaskAutostart : IAutostart
{
    /// <summary>Must match the <c>TaskId</c> of the startup task in <c>Package.appxmanifest</c>.</summary>
    public const string TaskId = "StereoKeeperStartup";

    private readonly IStartupTaskSwitch _task;
    private readonly Action _openStartupSettings;

    public StartupTaskAutostart()
        : this(new WindowsStartupTask(TaskId), () => ShellCommands.OpenUrl("ms-settings:startupapps"))
    {
    }

    internal StartupTaskAutostart(IStartupTaskSwitch task, Action openStartupSettings)
    {
        _task = task;
        _openStartupSettings = openStartupSettings;
    }

    public bool IsEnabled => TryRead(out var state) && IsOn(state);

    public bool TrySetEnabled(bool enabled)
    {
        if (!TryRead(out var state))
            return false;

        if (IsOn(state) == enabled)
            return true;

        try
        {
            if (!enabled)
            {
                // A policy that turns the task on cannot be overridden by the app.
                if (state == StartupTaskState.EnabledByPolicy)
                    return false;

                _task.Disable();
                return TryRead(out var after) && !IsOn(after);
            }

            switch (state)
            {
                case StartupTaskState.DisabledByUser:
                    _openStartupSettings();
                    return false;
                case StartupTaskState.DisabledByPolicy:
                    return false;
                default:
                    var result = _task.RequestEnable();
                    // The user may have turned the task off in Task Manager a moment ago.
                    if (result == StartupTaskState.DisabledByUser)
                        _openStartupSettings();

                    return IsOn(result);
            }
        }
        catch (Exception exception) when (IsWindowsFailure(exception))
        {
            CrashLog.Write("Changing the startup task failed", exception);
            return false;
        }
    }

    private bool TryRead(out StartupTaskState state)
    {
        try
        {
            state = _task.State;
            return true;
        }
        catch (Exception exception) when (IsWindowsFailure(exception))
        {
            CrashLog.Write("Reading the startup task failed", exception);
            state = StartupTaskState.Disabled;
            return false;
        }
    }

    private static bool IsOn(StartupTaskState state)
        => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    private static bool IsWindowsFailure(Exception exception)
        => exception is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
            or TimeoutException;
}

/// <summary>The startup task of the package, behind a seam for tests.</summary>
internal interface IStartupTaskSwitch
{
    StartupTaskState State { get; }
    StartupTaskState RequestEnable();
    void Disable();
}

/// <remarks>Looked up on every call: the user may change the task in Task Manager while the app runs.</remarks>
internal sealed class WindowsStartupTask(string taskId) : IStartupTaskSwitch
{
    public StartupTaskState State => StartupTaskInterop.GetState(taskId);

    public StartupTaskState RequestEnable() => StartupTaskInterop.RequestEnable(taskId);

    public void Disable() => StartupTaskInterop.Disable(taskId);
}
