using System.Runtime.InteropServices;
using StereoKeeper.Gui.Services;
using StereoKeeper.Windows;
using Xunit;

namespace StereoKeeper.Tests.Gui;

public sealed class StartupTaskAutostartTests
{
    [Theory]
    [InlineData(StartupTaskState.Enabled, true)]
    [InlineData(StartupTaskState.EnabledByPolicy, true)]
    [InlineData(StartupTaskState.Disabled, false)]
    [InlineData(StartupTaskState.DisabledByUser, false)]
    [InlineData(StartupTaskState.DisabledByPolicy, false)]
    public void TheSwitchShowsWhetherWindowsWillStartTheApp(StartupTaskState state, bool enabled)
        => Assert.Equal(enabled, Create(new FakeTask(state)).IsEnabled);

    [Fact]
    public void TurningOnAsksWindowsToEnableTheTask()
    {
        var task = new FakeTask(StartupTaskState.Disabled);
        var autostart = Create(task);

        Assert.True(autostart.TrySetEnabled(true));
        Assert.Equal(StartupTaskState.Enabled, task.State);
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void TurningOffDisablesTheTask()
    {
        var task = new FakeTask(StartupTaskState.Enabled);
        var autostart = Create(task);

        Assert.True(autostart.TrySetEnabled(false));
        Assert.Equal(StartupTaskState.Disabled, task.State);
    }

    [Fact]
    public void ATaskTheUserTurnedOffInWindowsOpensTheStartupSettings()
    {
        var task = new FakeTask(StartupTaskState.DisabledByUser);
        var opened = 0;
        var autostart = new StartupTaskAutostart(task, () => opened++);

        Assert.False(autostart.TrySetEnabled(true));
        Assert.Equal(1, opened);
        Assert.Equal(0, task.EnableRequests);
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void APolicyIsRespected()
    {
        var blocked = new FakeTask(StartupTaskState.DisabledByPolicy);
        Assert.False(Create(blocked).TrySetEnabled(true));
        Assert.Equal(0, blocked.EnableRequests);

        var forced = new FakeTask(StartupTaskState.EnabledByPolicy);
        Assert.True(Create(forced).TrySetEnabled(true));
        Assert.Equal(0, forced.DisableRequests);
    }

    [Fact]
    public void AWindowsFailureLeavesTheSwitchOff()
    {
        var task = new FakeTask(StartupTaskState.Disabled) { Failure = new InvalidOperationException("The process has no package identity.") };
        var autostart = Create(task);

        Assert.False(autostart.IsEnabled);
        Assert.False(autostart.TrySetEnabled(true));
    }

    [Fact]
    public void APolicyThatForcesTheTaskOnCannotBeTurnedOff()
    {
        var task = new FakeTask(StartupTaskState.EnabledByPolicy);
        var autostart = Create(task);

        Assert.False(autostart.TrySetEnabled(false));
        Assert.Equal(0, task.DisableRequests);
        Assert.True(autostart.IsEnabled);
    }

    [Fact]
    public void TurningOffReportsWhatWindowsDidNotWhatWasAsked()
    {
        var task = new FakeTask(StartupTaskState.Enabled) { IgnoreDisable = true };

        Assert.False(Create(task).TrySetEnabled(false));
        Assert.Equal(1, task.DisableRequests);
    }

    [Theory]
    [InlineData(StartupTaskState.Disabled)]
    [InlineData(StartupTaskState.DisabledByPolicy)]
    public void AnEnableRequestWindowsTurnsDownLeavesTheSwitchOff(StartupTaskState answer)
    {
        var task = new FakeTask(StartupTaskState.Disabled) { EnableAnswer = answer };

        Assert.False(Create(task).TrySetEnabled(true));
        Assert.Equal(1, task.EnableRequests);
    }

    [Fact]
    public void ATaskTheUserTurnsOffDuringTheRequestOpensTheStartupSettings()
    {
        var task = new FakeTask(StartupTaskState.Disabled) { EnableAnswer = StartupTaskState.DisabledByUser };
        var opened = 0;

        Assert.False(new StartupTaskAutostart(task, () => opened++).TrySetEnabled(true));
        Assert.Equal(1, opened);
    }

    [Theory]
    [InlineData(StartupTaskState.Enabled, true)]
    [InlineData(StartupTaskState.Disabled, false)]
    public void AskingForTheCurrentStateChangesNothing(StartupTaskState state, bool enabled)
    {
        var task = new FakeTask(state);

        Assert.True(Create(task).TrySetEnabled(enabled));
        Assert.Equal(0, task.EnableRequests);
        Assert.Equal(0, task.DisableRequests);
    }

    public static TheoryData<Exception> WindowsFailures => new()
    {
        // What Windows answers outside a package: HRESULT_FROM_WIN32(ERROR_NOT_FOUND).
        Marshal.GetExceptionForHR(unchecked((int)0x80070490))!,
        new InvalidOperationException("The package has no startup task."),
        new UnauthorizedAccessException("Access is denied."),
        new TimeoutException("Windows did not answer the startup task request."),
        new ArgumentException("The parameter is incorrect.")
    };

    [Theory]
    [MemberData(nameof(WindowsFailures))]
    public void AFailingReadNeverEscapes(Exception failure)
    {
        var task = new FakeTask(StartupTaskState.Enabled) { Failure = failure };
        var autostart = Create(task);

        Assert.False(autostart.IsEnabled);
        Assert.False(autostart.TrySetEnabled(true));
        Assert.False(autostart.TrySetEnabled(false));
        Assert.Equal(0, task.EnableRequests + task.DisableRequests);
    }

    [Theory]
    [MemberData(nameof(WindowsFailures))]
    public void AFailingChangeNeverEscapes(Exception failure)
    {
        var off = new FakeTask(StartupTaskState.Disabled) { ChangeFailure = failure };
        Assert.False(Create(off).TrySetEnabled(true));
        Assert.False(Create(off).IsEnabled);

        var on = new FakeTask(StartupTaskState.Enabled) { ChangeFailure = failure };
        Assert.False(Create(on).TrySetEnabled(false));
        Assert.True(Create(on).IsEnabled);
    }

    [Fact]
    public void AProgrammingErrorIsNotHidden()
    {
        var task = new FakeTask(StartupTaskState.Disabled) { ChangeFailure = new FormatException("A bug, not a Windows answer.") };

        Assert.Throws<FormatException>(() => Create(task).TrySetEnabled(true));
    }

    [Fact]
    public void TheSwitchFollowsChangesMadeInWindowsWhileTheAppRuns()
    {
        var task = new FakeTask(StartupTaskState.Enabled);
        var autostart = Create(task);
        Assert.True(autostart.IsEnabled);

        task.SetFromWindows(StartupTaskState.DisabledByUser);
        Assert.False(autostart.IsEnabled);

        task.SetFromWindows(StartupTaskState.Enabled);
        Assert.True(autostart.IsEnabled);
    }

    private static StartupTaskAutostart Create(FakeTask task)
        => new(task, () => throw new InvalidOperationException("The settings must not open."));

    private sealed class FakeTask(StartupTaskState state) : IStartupTaskSwitch
    {
        private StartupTaskState _state = state;

        /// <summary>Thrown by every call, as when the task cannot be found.</summary>
        public Exception? Failure { get; init; }

        /// <summary>Thrown by the enable and disable calls only.</summary>
        public Exception? ChangeFailure { get; init; }

        public StartupTaskState EnableAnswer { get; init; } = StartupTaskState.Enabled;
        public bool IgnoreDisable { get; init; }
        public int EnableRequests { get; private set; }
        public int DisableRequests { get; private set; }

        public StartupTaskState State => Failure is null ? _state : throw Failure;

        public void SetFromWindows(StartupTaskState state) => _state = state;

        public StartupTaskState RequestEnable()
        {
            if ((Failure ?? ChangeFailure) is { } failure)
                throw failure;

            EnableRequests++;
            _state = EnableAnswer;
            return _state;
        }

        public void Disable()
        {
            if ((Failure ?? ChangeFailure) is { } failure)
                throw failure;

            DisableRequests++;
            if (!IgnoreDisable)
                _state = StartupTaskState.Disabled;
        }
    }
}
