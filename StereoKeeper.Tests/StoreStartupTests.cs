using System.Runtime.InteropServices;
using StereoKeeper.App;
using StereoKeeper.Windows;
using Xunit;

namespace StereoKeeper.Tests;

public sealed class StoreStartupTests
{
    private static readonly LaunchOptions Shown = new(LaunchMode.Gui);
    private static readonly LaunchOptions Hidden = new(LaunchMode.Gui, StartHidden: true);

    [Fact]
    public void AStartupTaskLaunchWaitsInTheTrayEvenWithoutTheTraySwitch()
        => Assert.True(StoreStartup.Resolve(Shown, isPackaged: true, () => true).StartHidden);

    [Fact]
    public void AStartMenuLaunchOpensThePanel()
        => Assert.False(StoreStartup.Resolve(Shown, isPackaged: true, () => false).StartHidden);

    [Fact]
    public void TheTraySwitchIsEnoughAndWindowsIsNotAsked()
    {
        var asked = 0;
        Assert.Same(Hidden, StoreStartup.Resolve(Hidden, isPackaged: true, () => ++asked > 0));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ThePortableExeNeverAsksWindows()
    {
        var asked = 0;
        Assert.Same(Shown, StoreStartup.Resolve(Shown, isPackaged: false, () => ++asked > 0));
        Assert.Equal(0, asked);
    }

    public static TheoryData<Exception> WindowsFailures => new()
    {
        Marshal.GetExceptionForHR(unchecked((int)0x80004002))!, // E_NOINTERFACE
        new InvalidCastException("No such interface."),
        new UnauthorizedAccessException("Access is denied."),
        new InvalidOperationException("No activation."),
        new ArgumentException("The parameter is incorrect.")
    };

    [Theory]
    [MemberData(nameof(WindowsFailures))]
    public void AFailingQueryOpensThePanelInsteadOfFailingTheStart(Exception failure)
        => Assert.Same(Shown, StoreStartup.Resolve(Shown, isPackaged: true, () => throw failure));

    [Fact]
    public void ABugIsNotHidden()
        => Assert.Throws<FormatException>(() => StoreStartup.Resolve(Shown, isPackaged: true, () => throw new FormatException()));

    [Fact]
    public void OutsideAPackageTheRealQueryNeverReportsAStartupTask()
    {
        // Run on an STA thread like Main.
        bool? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = StartupTaskInterop.WasStartedByStartupTask();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The query hung.");

        Assert.True(failure is null or COMException, $"Unexpected failure: {failure}");
        Assert.NotEqual(true, result);
    }
}
