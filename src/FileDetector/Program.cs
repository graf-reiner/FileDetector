using FileDetector.Services;
using FileDetector.Ui;

namespace FileDetector;

internal static class Program
{
    private const string MutexName = @"Local\FileDetector.SingleInstance";
    private const string SignalName = @"Local\FileDetector.ShowSettings";

    [STAThread]
    private static void Main(string[] args)
    {
        var silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (args.Any(a => string.Equals(a, "--selftest-ui", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.RunUi();
            return;
        }

        var screenshotIndex = Array.FindIndex(args, a => string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase));
        if (screenshotIndex >= 0)
        {
            var outDir = screenshotIndex + 1 < args.Length ? args[screenshotIndex + 1] : Path.Combine(AppPaths.Root, "screenshots");
            Environment.ExitCode = SelfTest.CaptureUi(outDir);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask the live instance to surface its Settings window and get out of the way.
            SignalRunningInstance();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Info($"starting v{AppVersion.Full} (silent={silent}, exe={StartupRegistration.ExecutablePath})");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleFatal("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleFatal("unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        var context = new TrayApplicationContext(silent);
        using var signalListener = StartSignalListener(context);

        Application.Run(context);
        Log.Info("stopped");
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(SignalName);
            handle.Set();
        }
        catch (Exception ex)
        {
            // Not fatal — the user simply sees nothing happen beyond the existing tray icon.
            Log.Warn($"another instance is running but could not be signalled: {ex.Message}");
        }
    }

    /// <summary>Watches for a second launch and brings this instance's Settings window forward.</summary>
    private static IDisposable StartSignalListener(TrayApplicationContext context)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        var cts = new CancellationTokenSource();

        var thread = new Thread(() =>
        {
            var waits = new[] { handle, cts.Token.WaitHandle };
            while (!cts.IsCancellationRequested)
            {
                var index = WaitHandle.WaitAny(waits);
                if (index != 0) return;
                context.ShowSettingsFromAnotherInstance();
            }
        })
        {
            IsBackground = true,
            Name = "FileDetector.SecondInstanceListener",
        };
        thread.Start();

        return new Disposer(() =>
        {
            cts.Cancel();
            handle.Dispose();
            cts.Dispose();
        });
    }

    private static void HandleFatal(string what, Exception? ex)
    {
        Log.Error(what, ex);
        try
        {
            MessageBox.Show(
                $"FileDetector hit an unexpected error and may not be watching correctly.{Environment.NewLine}{Environment.NewLine}" +
                $"{ex?.Message}{Environment.NewLine}{Environment.NewLine}Details were written to:{Environment.NewLine}{AppPaths.LogFile}",
                "FileDetector", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing more we can do from here.
        }
    }

    private sealed class Disposer(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
