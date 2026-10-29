using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using HomeworkValidator.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace HomeworkValidator.App.Tests;

/// <summary>Runs the real app headlessly, rendering with Skia so tests can save screenshots.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Keeps tests from touching the real settings and saved answers.</summary>
    [ModuleInitializer]
    internal static void UseTemporaryAppData() =>
        Environment.SetEnvironmentVariable("HV_APP_DATA", Path.Combine(Path.GetTempPath(), "hv-app-tests-" + Environment.ProcessId));

    /// <summary>A new empty folder for one test's settings and saved answers.</summary>
    public static string NewTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "hv-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Saves what the window looks like to bin/.../screenshots/&lt;name&gt;.png, for a person to look at.</summary>
    public static string SaveScreenshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        return path;
    }
}
