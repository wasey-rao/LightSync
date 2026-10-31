using System.Threading;
using System.Windows;

namespace LightSync;

public partial class App : Application
{
    const string InstanceMutexName = "LightSync.SingleInstance";
    const string ShowEventName = "LightSync.ShowWindow";

    Mutex? _instanceMutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running (probably in the tray): bring that window forward instead.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* other instance is exiting */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"Unhandled UI error: {args.Exception}");
            args.Handled = true;
        };

        var settings = AppSettings.Load();
        var window = new MainWindow(settings);
        MainWindow = window;
        SessionEnding += (_, _) => window.ExitApp();

        bool startHidden = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) || settings.StartMinimized;
        if (!startHidden) window.Show();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.InvokeAsync(window.ShowFromTray);
        }) { IsBackground = true, Name = "Instance signal" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
