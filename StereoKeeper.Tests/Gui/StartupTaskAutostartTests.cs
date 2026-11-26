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

    private static StartupTaskAutostart Create(FakeTask task)
        => new(task, () => throw new InvalidOperationException("The settings must not open."));

    private sealed class FakeTask(StartupTaskState state) : IStartupTaskSwitch
    {
        private StartupTaskState _state = state;

        public Exception? Failure { get; init; }
        public int EnableRequests { get; private set; }
        public int DisableRequests { get; private set; }

        public StartupTaskState State => Failure is null ? _state : throw Failure;

        public StartupTaskState RequestEnable()
        {
            EnableRequests++;
            _state = StartupTaskState.Enabled;
            return _state;
        }

        public void Disable()
        {
            DisableRequests++;
            _state = StartupTaskState.Disabled;
        }
    }
}
