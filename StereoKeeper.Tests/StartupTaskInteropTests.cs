using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using StereoKeeper.Gui.Services;
using StereoKeeper.Windows;
using Xunit;

namespace StereoKeeper.Tests;

/// <summary>
/// StartupTaskInterop calls WinRT through hand-written vtable slots. These tests read the
/// interface IDs, method order and enum values from the Windows metadata on this PC
/// (System32\WinMetadata), the same source the official projections are generated from, so a
/// wrong slot or IID fails here instead of crashing the Store build. The live tests then call the
/// real API outside a package, where Windows must refuse cleanly.
/// </summary>
public sealed class StartupTaskInteropTests
{
    // IUnknown (3 methods) and IInspectable (3 methods) come before an interface's own methods.
    private const int FirstOwnSlot = 6;

    private static readonly string MetadataDirectory = Path.Combine(Environment.SystemDirectory, "WinMetadata");

    [Fact]
    public void TheStaticsInterfaceMatchesWindows()
    {
        using var metadata = WinMetadata.Open("Windows.ApplicationModel.winmd");
        var statics = metadata.GetType("Windows.ApplicationModel", "IStartupTaskStatics");

        Assert.Equal(StartupTaskInterop.IidStartupTaskStatics, metadata.GetGuid(statics));
        Assert.Equal(StartupTaskInterop.StaticsGetAsync, metadata.GetSlot(statics, "GetAsync"));
        Assert.Contains(
            metadata.GetStaticInterfaces(metadata.GetType("Windows.ApplicationModel", "StartupTask")),
            name => name == "IStartupTaskStatics");
    }

    [Fact]
    public void TheTaskInterfaceMatchesWindows()
    {
        using var metadata = WinMetadata.Open("Windows.ApplicationModel.winmd");
        var task = metadata.GetType("Windows.ApplicationModel", "IStartupTask");

        Assert.Equal(StartupTaskInterop.IidStartupTask, metadata.GetGuid(task));
        Assert.Equal(StartupTaskInterop.TaskRequestEnableAsync, metadata.GetSlot(task, "RequestEnableAsync"));
        Assert.Equal(StartupTaskInterop.TaskDisable, metadata.GetSlot(task, "Disable"));
        Assert.Equal(StartupTaskInterop.TaskGetState, metadata.GetSlot(task, "get_State"));
    }

    [Fact]
    public void TheStateValuesMatchWindows()
    {
        using var metadata = WinMetadata.Open("Windows.ApplicationModel.winmd");
        var values = metadata.GetEnumValues(metadata.GetType("Windows.ApplicationModel", "StartupTaskState"));

        Assert.Equal(
            Enum.GetValues<StartupTaskState>().ToDictionary(value => value.ToString(), value => (int)value),
            values);
    }

    [Fact]
    public void TheAsyncInterfacesMatchWindows()
    {
        using var metadata = WinMetadata.Open("Windows.Foundation.winmd");
        var info = metadata.GetType("Windows.Foundation", "IAsyncInfo");
        var operation = metadata.GetType("Windows.Foundation", "IAsyncOperation`1");
        var status = metadata.GetEnumValues(metadata.GetType("Windows.Foundation", "AsyncStatus"));

        Assert.Equal(StartupTaskInterop.IidAsyncInfo, metadata.GetGuid(info));
        Assert.Equal(StartupTaskInterop.AsyncInfoGetStatus, metadata.GetSlot(info, "get_Status"));
        Assert.Equal(StartupTaskInterop.AsyncInfoGetErrorCode, metadata.GetSlot(info, "get_ErrorCode"));
        Assert.Equal(StartupTaskInterop.AsyncInfoCancel, metadata.GetSlot(info, "Cancel"));
        Assert.Equal(StartupTaskInterop.AsyncOperationGetResults, metadata.GetSlot(operation, "GetResults"));
        Assert.Equal(StartupTaskInterop.AsyncStatusStarted, status["Started"]);
        Assert.Equal(StartupTaskInterop.AsyncStatusCompleted, status["Completed"]);
    }

    [Fact]
    public void TheActivationInterfacesMatchWindows()
    {
        using var metadata = WinMetadata.Open("Windows.ApplicationModel.winmd");
        var statics = metadata.GetType("Windows.ApplicationModel", "IAppInstanceStatics");
        var args = metadata.GetType("Windows.ApplicationModel.Activation", "IActivatedEventArgs");
        var kinds = metadata.GetEnumValues(metadata.GetType("Windows.ApplicationModel.Activation", "ActivationKind"));

        Assert.Equal(StartupTaskInterop.IidAppInstanceStatics, metadata.GetGuid(statics));
        Assert.Equal(StartupTaskInterop.AppInstanceGetActivatedEventArgs, metadata.GetSlot(statics, "GetActivatedEventArgs"));
        Assert.Contains(
            metadata.GetStaticInterfaces(metadata.GetType("Windows.ApplicationModel", "AppInstance")),
            name => name == "IAppInstanceStatics");
        Assert.Equal(StartupTaskInterop.IidActivatedEventArgs, metadata.GetGuid(args));
        Assert.Equal(StartupTaskInterop.ActivatedEventArgsGetKind, metadata.GetSlot(args, "get_Kind"));
        Assert.Equal(StartupTaskInterop.ActivationKindStartupTask, kinds["StartupTask"]);
    }

    [Fact]
    public void WindowsCanCreateTheStartupTaskClass()
        => Assert.True(StartupTaskInterop.CanActivate());

    [Theory]
    [InlineData(ApartmentState.MTA)]
    [InlineData(ApartmentState.STA)]
    public void OutsideAPackageWindowsRefusesQuicklyAndCleanly(ApartmentState apartment)
    {
        // The panel runs on an STA thread; the call must neither hang nor crash there.
        var outcome = RunOn(apartment, () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var exception = Record.Exception(() => StartupTaskInterop.GetState(StartupTaskAutostart.TaskId));
            return (exception, stopwatch.Elapsed);
        });

        Assert.IsType<COMException>(outcome.exception);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(3), $"Windows took {outcome.Elapsed} to answer.");
    }

    [Fact]
    public void OutsideAPackageTheRealSwitchStaysOffWithoutThrowing()
    {
        var autostart = new StartupTaskAutostart(
            new WindowsStartupTask(StartupTaskAutostart.TaskId),
            () => throw new InvalidOperationException("The settings must not open."));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(autostart.IsEnabled);
            Assert.False(autostart.TrySetEnabled(true));
            Assert.False(autostart.TrySetEnabled(false));
        }
    }

    [Fact]
    public void ManyCallsFromManyThreadsLeakNothingAndNeverCrash()
    {
        // Every call activates, queries and releases several COM objects; a missing Release or a
        // double Release shows up as a crash or as growing handle counts over many calls.
        using var process = Process.GetCurrentProcess();
        var handlesBefore = process.HandleCount;

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            Assert.True(StartupTaskInterop.CanActivate());
            Assert.IsType<COMException>(Record.Exception(() => StartupTaskInterop.GetState(StartupTaskAutostart.TaskId)));
        });

        process.Refresh();
        Assert.True(
            process.HandleCount - handlesBefore < 100,
            $"Handles grew from {handlesBefore} to {process.HandleCount}.");
    }

    private static T RunOn<T>(ApartmentState apartment, Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(apartment);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The call hung.");
        if (failure is not null)
            throw new InvalidOperationException("The call failed on the test thread.", failure);

        return result;
    }

    /// <summary>A small reader for the Windows Runtime metadata files.</summary>
    private sealed class WinMetadata : IDisposable
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _reader;

        private WinMetadata(string path)
        {
            _pe = new PEReader(File.OpenRead(path));
            _reader = _pe.GetMetadataReader();
        }

        public static WinMetadata Open(string fileName)
        {
            var path = Path.Combine(MetadataDirectory, fileName);
            Assert.True(File.Exists(path), $"{path} is missing.");
            return new WinMetadata(path);
        }

        public TypeDefinition GetType(string ns, string name)
        {
            foreach (var handle in _reader.TypeDefinitions)
            {
                var type = _reader.GetTypeDefinition(handle);
                if (_reader.StringComparer.Equals(type.Namespace, ns) && _reader.StringComparer.Equals(type.Name, name))
                    return type;
            }

            throw new InvalidOperationException($"{ns}.{name} is not in the Windows metadata.");
        }

        /// <summary>The vtable slot of a method: its position in the interface plus IInspectable.</summary>
        public int GetSlot(TypeDefinition type, string methodName)
        {
            var methods = type.GetMethods()
                .Select(handle => _reader.GetString(_reader.GetMethodDefinition(handle).Name))
                .ToList();
            var index = methods.IndexOf(methodName);
            Assert.True(index >= 0, $"{methodName} is not a method of {_reader.GetString(type.Name)}.");
            return FirstOwnSlot + index;
        }

        public Guid GetGuid(TypeDefinition type)
        {
            var blob = FindAttribute(type, "GuidAttribute").Single();
            var reader = _reader.GetBlobReader(blob);
            Assert.Equal(1, reader.ReadUInt16()); // custom attribute prolog
            var a = reader.ReadUInt32();
            var b = reader.ReadUInt16();
            var c = reader.ReadUInt16();
            var rest = reader.ReadBytes(8);
            return new Guid(a, b, c, rest[0], rest[1], rest[2], rest[3], rest[4], rest[5], rest[6], rest[7]);
        }

        /// <summary>The interfaces named by [Static] on a runtime class.</summary>
        public IEnumerable<string> GetStaticInterfaces(TypeDefinition type)
        {
            foreach (var blob in FindAttribute(type, "StaticAttribute"))
            {
                var reader = _reader.GetBlobReader(blob);
                Assert.Equal(1, reader.ReadUInt16());
                var typeName = reader.ReadSerializedString() ?? "";
                yield return typeName[(typeName.LastIndexOf('.') + 1)..];
            }
        }

        public Dictionary<string, int> GetEnumValues(TypeDefinition type)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var handle in type.GetFields())
            {
                var field = _reader.GetFieldDefinition(handle);
                var constant = field.GetDefaultValue();
                if (constant.IsNil)
                    continue; // value__

                var blob = _reader.GetBlobReader(_reader.GetConstant(constant).Value);
                values[_reader.GetString(field.Name)] = blob.Length == 4 ? blob.ReadInt32() : (int)blob.ReadUInt32();
            }

            return values;
        }

        private IEnumerable<BlobHandle> FindAttribute(TypeDefinition type, string attributeName)
        {
            foreach (var handle in type.GetCustomAttributes())
            {
                var attribute = _reader.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference)
                    continue;

                var parent = _reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind == HandleKind.TypeReference &&
                    _reader.StringComparer.Equals(_reader.GetTypeReference((TypeReferenceHandle)parent).Name, attributeName))
                {
                    yield return attribute.Value;
                }
            }
        }

        public void Dispose() => _pe.Dispose();
    }
}
