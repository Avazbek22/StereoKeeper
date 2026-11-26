using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StereoKeeper.Windows;

/// <summary>Values of <c>Windows.ApplicationModel.StartupTaskState</c>.</summary>
public enum StartupTaskState
{
    Disabled = 0,
    DisabledByUser = 1,
    Enabled = 2,
    DisabledByPolicy = 3,
    EnabledByPolicy = 4
}

/// <summary>
/// The package startup task (<c>Windows.ApplicationModel.StartupTask</c>) called through its raw
/// WinRT interfaces: the C#/WinRT projection does not trim cleanly, and three calls do not
/// justify it. Works only in a process with package identity.
/// </summary>
internal static unsafe class StartupTaskInterop
{
    internal const string ClassName = "Windows.ApplicationModel.StartupTask";

    // The interface IDs and vtable slots below are checked against the Windows metadata in
    // System32\WinMetadata by StartupTaskInteropTests, so a typo cannot reach a user.
    internal static readonly Guid IidStartupTaskStatics = new("ee5b60bd-a148-41a7-b26e-e8b88a1e62f8");
    internal static readonly Guid IidStartupTask = new("f75c23c8-b5f2-4f6c-88dd-36cb1d599d17");
    internal static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    // The calls run on the UI thread and normally finish within milliseconds; a hung broker must
    // not freeze the panel for long.
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(5);

    // Slots after IUnknown (0-2) and IInspectable (3-5).
    internal const int StaticsGetAsync = 7;
    internal const int TaskRequestEnableAsync = 6;
    internal const int TaskDisable = 7;
    internal const int TaskGetState = 8;
    internal const int AsyncInfoGetStatus = 7;
    internal const int AsyncInfoGetErrorCode = 8;
    internal const int AsyncInfoCancel = 9;
    internal const int AsyncOperationGetResults = 8;

    internal const string AppInstanceClassName = "Windows.ApplicationModel.AppInstance";
    internal static readonly Guid IidAppInstanceStatics = new("9d11e77f-9ea6-47af-a6ec-46784c5ba254");
    internal static readonly Guid IidActivatedEventArgs = new("cf651713-cd08-4fd8-b697-a281b6544e2e");
    internal const int AppInstanceGetActivatedEventArgs = 7;
    internal const int ActivatedEventArgsGetKind = 6;
    internal const int ActivationKindStartupTask = 1020;

    internal const int AsyncStatusStarted = 0;
    internal const int AsyncStatusCompleted = 1;

    public static StartupTaskState GetState(string taskId)
        => WithTask(taskId, task =>
        {
            int state;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(task, TaskGetState))(task, &state));
            return (StartupTaskState)state;
        });

    public static StartupTaskState RequestEnable(string taskId)
        => WithTask(taskId, task =>
        {
            nint operation;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(task, TaskRequestEnableAsync))(task, &operation));
            try
            {
                Wait(operation);
                int state;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(operation, AsyncOperationGetResults))(operation, &state));
                return (StartupTaskState)state;
            }
            finally
            {
                Marshal.Release(operation);
            }
        });

    public static void Disable(string taskId)
        => WithTask(taskId, task =>
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(task, TaskDisable))(task));
            return 0;
        });

    private static T WithTask<T>(string taskId, Func<nint, T> use)
    {
        var task = GetTask(taskId);
        try
        {
            return use(task);
        }
        finally
        {
            Marshal.Release(task);
        }
    }

    private static nint GetTask(string taskId)
    {
        var statics = GetActivationFactory();
        try
        {
            var id = CreateString(taskId);
            nint operation;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, StaticsGetAsync))(statics, id, &operation));
            }
            finally
            {
                _ = WindowsDeleteString(id);
            }

            try
            {
                Wait(operation);
                nint result;
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(operation, AsyncOperationGetResults))(operation, &result));
                if (result == 0)
                    throw new InvalidOperationException($"The package has no startup task '{taskId}'.");

                // GetResults hands out the runtime class's default interface; ask for it by name anyway.
                try
                {
                    var iid = IidStartupTask;
                    Check(Marshal.QueryInterface(result, in iid, out var task));
                    return task;
                }
                finally
                {
                    Marshal.Release(result);
                }
            }
            finally
            {
                Marshal.Release(operation);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>
    /// Whether Windows started this process through the package startup task, that is at sign-in.
    /// Asked from the activation itself, so it holds even if Windows leaves out the startup
    /// parameters. Works only in a process with package identity; outside one it is false.
    /// </summary>
    public static bool WasStartedByStartupTask()
    {
        var statics = GetActivationFactory(AppInstanceClassName, IidAppInstanceStatics);
        try
        {
            nint args;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(statics, AppInstanceGetActivatedEventArgs))(statics, &args));
            if (args == 0)
                return false;

            try
            {
                var iid = IidActivatedEventArgs;
                Check(Marshal.QueryInterface(args, in iid, out var activated));
                try
                {
                    int kind;
                    Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(activated, ActivatedEventArgsGetKind))(activated, &kind));
                    return kind == ActivationKindStartupTask;
                }
                finally
                {
                    Marshal.Release(activated);
                }
            }
            finally
            {
                Marshal.Release(args);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    /// <summary>
    /// Whether Windows can create the startup task class at all; true on every supported Windows,
    /// with or without a package. For the tests, which run outside a package.
    /// </summary>
    internal static bool CanActivate()
    {
        var factory = GetActivationFactory(ClassName, IidStartupTaskStatics);
        Marshal.Release(factory);
        return factory != 0;
    }

    private static nint GetActivationFactory() => GetActivationFactory(ClassName, IidStartupTaskStatics);

    private static nint GetActivationFactory(string className, Guid interfaceId)
    {
        var name = CreateString(className);
        try
        {
            var iid = interfaceId;
            Check(RoGetActivationFactory(name, &iid, out var factory));
            return factory;
        }
        finally
        {
            _ = WindowsDeleteString(name);
        }
    }

    /// <summary>Waits for a WinRT async operation. These finish within milliseconds.</summary>
    private static void Wait(nint operation)
    {
        var iid = IidAsyncInfo;
        Check(Marshal.QueryInterface(operation, in iid, out var info));
        try
        {
            var getStatus = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, AsyncInfoGetStatus);
            var stopwatch = Stopwatch.StartNew();
            int status;
            while (true)
            {
                Check(getStatus(info, &status));
                if (status != AsyncStatusStarted)
                    break;

                if (stopwatch.Elapsed > AsyncTimeout)
                {
                    // Let Windows drop the request instead of finishing it after we gave up.
                    _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(info, AsyncInfoCancel))(info);
                    throw new TimeoutException("Windows did not answer the startup task request.");
                }

                Thread.Sleep(5);
            }

            if (status != AsyncStatusCompleted)
            {
                int error;
                Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(info, AsyncInfoGetErrorCode))(info, &error));
                Marshal.ThrowExceptionForHR(error < 0 ? error : unchecked((int)0x80004004)); // E_ABORT when cancelled
            }
        }
        finally
        {
            Marshal.Release(info);
        }
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    private static nint CreateString(string value)
    {
        fixed (char* chars = value)
        {
            Check(WindowsCreateString(chars, (uint)value.Length, out var handle));
            return handle;
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsCreateString(char* sourceString, uint length, out nint handle);

    [DllImport("combase.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsDeleteString(nint handle);
}
