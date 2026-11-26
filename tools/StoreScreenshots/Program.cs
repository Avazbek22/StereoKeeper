using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using StereoKeeper.App;
using StereoKeeper.Core;
using StereoKeeper.Gui;
using StereoKeeper.Gui.Model;
using StereoKeeper.Gui.Services;
using StereoKeeper.Gui.ViewModels;
using StereoKeeper.Localization;

namespace StereoKeeper.StoreScreenshots;

/// <summary>
/// Renders the panel off-screen in six states and lays them out as the two Store screenshots of
/// the README: the main states (call mode, stereo back, Always stereo) and the rest (progress,
/// settings, help). Every interface language gets its own pair, with the same demo data.
/// </summary>
internal static class Program
{
    // The Store takes desktop screenshots from 1366 x 768 to 3840 x 2160. Full HD at 130% keeps
    // the text sharp on high-DPI screens; at 130% the panels keep a margin like the README images.
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;
    private const double Scale = 1.3;
    private const int Gap = 48;
    // Darker than the panel (#1C1C1F), so the dark panels stand out the way they do on a dark desktop.
    private static readonly SKColor Background = new(0x0E, 0x0E, 0x11);

    /// <remarks>
    /// One language per run: the text renderer remembers the fallback font it picked for a
    /// character, so a second language in the same process (Japanese after Chinese) could come out
    /// in the first one's font. New-StoreScreenshots.ps1 starts a run per language.
    /// </remarks>
    private static int Main(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: StoreScreenshots <output directory> <language code, for example en-US>");

        var output = Path.GetFullPath(args[0]);
        var language = AppLanguageCatalog.All.SingleOrDefault(
                           candidate => string.Equals(candidate.Code, args[1], StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"'{args[1]}' is not an interface language.");

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        session.Dispatch(
            async () =>
            {
                Text.SetLanguage(language.Language);
                UseNativeFont(language.Code);
                var directory = Path.Combine(output, StoreLanguage(language.Code));
                Directory.CreateDirectory(directory);

                var main = new[] { await RenderAsync(CallMode), await RenderAsync(StereoBack), await RenderAsync(AlwaysStereo) };
                var more = new[] { await RenderAsync(Working), await RenderAsync(Settings), await RenderAsync(Help) };

                Save(Compose(main), Path.Combine(directory, "01-panel.png"));
                Save(Compose(more), Path.Combine(directory, "02-screens.png"));
                foreach (var card in main.Concat(more))
                    card.Dispose();

                Console.WriteLine($"{language.Code,-6} {directory}");
                return true;
            },
            CancellationToken.None).GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>
    /// Han characters look different in Chinese, Japanese and Korean, and without a hint the text
    /// renderer mixes the fonts of these languages, even inside one word. The screenshots show
    /// the panel as on a PC set to that language, where Windows uses its own font for it.
    /// </summary>
    private static void UseNativeFont(string code)
    {
        var font = code switch
        {
            "zh-CN" => "Microsoft YaHei UI",
            "zh-TW" => "Microsoft JhengHei UI",
            "ja-JP" => "Yu Gothic UI",
            "ko-KR" => "Malgun Gothic",
            _ => null
        };

        if (font is null)
            return;

        var resources = Application.Current?.Resources ?? throw new InvalidOperationException("The app is not running.");
        resources["TextFont"] = new FontFamily($"Segoe UI Variable Text, Segoe UI, {font}");
        resources["DisplayFont"] = new FontFamily($"Segoe UI Variable Display, Segoe UI, {font}");
    }

    /// <summary>The folder name the Store listing uses for the language.</summary>
    private static string StoreLanguage(string code)
        => string.Equals(code, "es-419", StringComparison.OrdinalIgnoreCase) ? "es-mx" : code.ToLowerInvariant();

    // ----- The six states, with the README's demo data -----

    private static async Task<Panel> CallMode(Demo demo)
    {
        var panel = demo.Create(Demo.CallMode());
        await Task.CompletedTask;
        return panel;
    }

    private static async Task<Panel> StereoBack(Demo demo)
    {
        var panel = demo.Create(Demo.CallMode());
        await panel.ViewModel.RunAsync(OperationKind.Soft);
        panel.ViewModel.ApplyStatus(Demo.Stereo());
        return panel;
    }

    private static async Task<Panel> AlwaysStereo(Demo demo)
    {
        var panel = demo.Create(Demo.AlwaysStereo());
        await Task.CompletedTask;
        return panel;
    }

    private static async Task<Panel> Working(Demo demo)
    {
        // The first service is restarted, the second one is being stopped and Windows takes its time.
        demo.Runner.HoldUntilReleased = true;
        demo.Runner.Events.Add(Step(OperationStep.Stopping, Demo.HandsFree));
        demo.Runner.Events.Add(Step(OperationStep.Starting, Demo.HandsFree));
        demo.Runner.Events.Add(Step(OperationStep.Stopping, Demo.AudioGateway));
        demo.Runner.Events.Add(new OperationEvent(
            OperationEventKind.Waiting, "", TimeSpan.FromSeconds(3), OperationStep.WaitingStop, Demo.AudioGateway));

        var panel = demo.Create(Demo.CallMode());
        panel.Pending = panel.ViewModel.RunAsync(OperationKind.Soft);
        await Task.Yield();
        panel.ViewModel.Tick();
        return panel;

        static OperationEvent Step(OperationStep step, string service) => new(OperationEventKind.Step, "", Step: step, Service: service);
    }

    private static async Task<Panel> Settings(Demo demo)
    {
        var panel = demo.Create(Demo.Stereo());
        panel.ViewModel.ShowSettingsCommand.Execute(null);
        await Task.CompletedTask;
        return panel;
    }

    private static async Task<Panel> Help(Demo demo)
    {
        var panel = demo.Create(Demo.Stereo());
        panel.ViewModel.ShowHelpCommand.Execute(null);
        await Task.CompletedTask;
        return panel;
    }

    // ----- Rendering -----

    /// <summary>Shows the panel in a window, lets it settle and cuts out the card without its shadow.</summary>
    private static async Task<SKBitmap> RenderAsync(Func<Demo, Task<Panel>> state)
    {
        var demo = new Demo();
        var panel = await state(demo);
        var window = new FlyoutWindow { DataContext = panel.ViewModel };
        window.SetRenderScaling(Scale);
        window.Show();
        try
        {
            var card = window.FindControl<Border>("Card") ?? throw new InvalidOperationException("The panel has no card.");
            card.BoxShadow = default;
            Pump();
            panel.ViewModel.Tick();
            Pump();

            var origin = card.TranslatePoint(default, window) ?? throw new InvalidOperationException("The card is not in the window.");
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            var bounds = new PixelRect(
                (int)Math.Round(origin.X * Scale),
                (int)Math.Round(origin.Y * Scale),
                (int)Math.Round(card.Bounds.Width * Scale),
                (int)Math.Round(card.Bounds.Height * Scale));
            return Crop(frame, bounds);
        }
        finally
        {
            window.Close();
            if (panel.Pending is { } pending)
            {
                demo.Runner.Release();
                await pending;
            }
        }
    }

    private static SKBitmap Crop(WriteableBitmap frame, PixelRect bounds)
    {
        using var buffer = frame.Lock();
        var colorType = buffer.Format == PixelFormats.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
        var alphaType = buffer.AlphaFormat == AlphaFormat.Unpremul ? SKAlphaType.Unpremul : SKAlphaType.Premul;
        var info = new SKImageInfo(buffer.Size.Width, buffer.Size.Height, colorType, alphaType);

        using var source = new SKBitmap();
        if (!source.InstallPixels(info, buffer.Address, buffer.RowBytes))
            throw new InvalidOperationException("The rendered frame could not be read.");

        if (bounds.Right > info.Width || bounds.Bottom > info.Height)
            throw new InvalidOperationException($"The card {bounds} does not fit the frame {info.Width} x {info.Height}.");

        var card = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(card);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height), SKRect.Create(bounds.Width, bounds.Height));
        return card;
    }

    /// <summary>Three cards side by side, centered on the Store canvas.</summary>
    private static SKBitmap Compose(IReadOnlyList<SKBitmap> cards)
    {
        var width = cards.Sum(card => card.Width) + (Gap * (cards.Count - 1));
        var height = cards.Max(card => card.Height);
        if (width > CanvasWidth || height > CanvasHeight)
            throw new InvalidOperationException($"The cards ({width} x {height}) do not fit the {CanvasWidth} x {CanvasHeight} canvas.");

        // Opaque: the Store shows screenshots on its own background, and an opaque PNG is smaller.
        var result = new SKBitmap(CanvasWidth, CanvasHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(result);
        canvas.Clear(Background);
        var x = (CanvasWidth - width) / 2;
        var y = (CanvasHeight - height) / 2;
        foreach (var card in cards)
        {
            canvas.DrawBitmap(card, x, y);
            x += card.Width + Gap;
        }

        return result;
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using (bitmap)
        using (var pixels = bitmap.PeekPixels())
        using (var data = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9))
                          ?? throw new InvalidOperationException($"{path} could not be encoded."))
        using (var file = File.Create(path))
        {
            data.SaveTo(file);
        }
    }

    /// <summary>Lets bindings, layout and the page animations finish.</summary>
    private static void Pump()
    {
        for (var tick = 0; tick < 60; tick++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(12);
        }
    }
}

/// <summary>The real app and theme, rendered by Skia without a screen.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<GuiApp>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class Panel(FlyoutViewModel viewModel)
{
    public FlyoutViewModel ViewModel { get; } = viewModel;

    /// <summary>An operation held open for the progress screenshot.</summary>
    public Task? Pending { get; set; }
}

/// <summary>The demo PC of the README: Sony headphones, Discord, Chrome and Teams on the microphone.</summary>
internal sealed class Demo
{
    public const string HeadsetName = "WH-1000XM5";
    public const string HandsFree = "Bluetooth Hands-Free Service (HFP)";
    public const string AudioGateway = "Bluetooth Audio Gateway (HFP)";

    private static readonly CaptureDevice HeadsetMicrophone =
        new("bt-headset", $"Headset ({HeadsetName})", IsBluetooth: true, IsDefault: true, IsDefaultCommunications: true);

    private static readonly CaptureDevice UsbMicrophone =
        new("usb", "Microphone (fifine)", IsBluetooth: false, IsDefault: false, IsDefaultCommunications: false);

    private static readonly string[] Apps = ["Discord", "Google Chrome", "Microsoft Teams"];

    public DemoRunner Runner { get; } = new();

    public Panel Create(LiveStatus status)
    {
        var viewModel = new FlyoutViewModel(Runner, new DemoAutostart(), new DemoSettings(), new DemoShell());
        viewModel.ApplyStatus(status);
        return new Panel(viewModel);
    }

    public static LiveStatus CallMode() => LiveStatus.Evaluate(
        Services(ServiceRunState.Running, ServiceStartType.Manual),
        MicrophoneDiagnostics.Analyze(new MicrophoneProbeResult(
            [HeadsetMicrophone, UsbMicrophone],
            Apps.Select((app, index) => new CaptureClient(
                HeadsetMicrophone.Id,
                new AppIdentity(100 + index, app, $@"C:\Apps\{app}.exe", null))).ToList(),
            null) { Headsets = [HeadsetName] }));

    public static LiveStatus Stereo() => LiveStatus.Evaluate(
        Services(ServiceRunState.Running, ServiceStartType.Manual),
        MicrophoneDiagnostics.Analyze(new MicrophoneProbeResult([HeadsetMicrophone, UsbMicrophone], [], null) { Headsets = [HeadsetName] }));

    /// <summary>Always stereo removes the headset microphone; the headphones still play.</summary>
    public static LiveStatus AlwaysStereo() => LiveStatus.Evaluate(
        Services(ServiceRunState.Stopped, ServiceStartType.Disabled),
        MicrophoneDiagnostics.Analyze(new MicrophoneProbeResult([UsbMicrophone], [], null) { Headsets = [HeadsetName] }));

    private static List<ServiceSnapshot> Services(ServiceRunState runState, ServiceStartType startType)
        =>
        [
            new("BthHFSrv", HandsFree, true, true, runState, startType, 3, null),
            new("BTAGService", AudioGateway, true, true, runState, startType, 3, null)
        ];
}

internal sealed class DemoRunner : IOperationRunner
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldUntilReleased { get; set; }

    public List<OperationEvent> Events { get; } = [];

    public void Release() => _release.TrySetResult();

    public async Task<OperationOutcome> RunAsync(
        OperationKind operation,
        Action<OperationEvent> onEvent,
        Action? onElevated = null,
        CancellationToken cancellationToken = default)
    {
        onElevated?.Invoke();
        foreach (var operationEvent in Events)
            onEvent(operationEvent);

        if (HoldUntilReleased)
            await _release.Task.ConfigureAwait(true);

        return OperationOutcome.Completed(new ToggleReport("demo", true, [], [], false));
    }
}

internal sealed class DemoAutostart : IAutostart
{
    public bool IsEnabled => false;

    public bool TrySetEnabled(bool enabled) => false;
}

internal sealed class DemoSettings : IGuiSettingsStore
{
    public GuiSettings Load() => new();

    public void Save(GuiSettings settings)
    {
    }
}

internal sealed class DemoShell : IShell
{
    public void OpenUrl(string url)
    {
    }

    public void OpenSoundRecordingSettings()
    {
    }

    public Task CopyTextAsync(string text) => Task.CompletedTask;

    public void Quit()
    {
    }
}
