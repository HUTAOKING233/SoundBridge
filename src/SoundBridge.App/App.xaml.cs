using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoundBridge.App;

public partial class App : Application
{
    private TrayService? _tray;
    private DeviceWatcher? _pairedDevices;
    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationWait;
    private static readonly object LogGate = new();

    // Register before WPF constructs the application so startup failures are logged too.
    static App() =>
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("Unhandled: " + args.ExceptionObject);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => Log("UnobservedTask: " + args.Exception);

        base.OnStartup(e);
        try
        {
            if (!TryStartPrimaryInstance())
            {
                Shutdown();
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            Log($"Single-instance initialization failed: {ex.Message}");
            MessageBox.Show("无法访问声桥的现有实例。请退出旧实例后重试。", "声桥");
            Shutdown(1);
            return;
        }

        Log("Application start");
        var settings = new AppSettings();
        Log($"Automatic audio recovery: {(settings.AutoWakeEnabled ? "on" : "off")}");
        StartPairingLog();
        var window = new MainWindow(settings);
        MainWindow = window;

        try
        {
            _tray = new TrayService(window, settings);
        }
        catch (Exception ex)
        {
            Log("Tray service failed: " + ex);
        }

        if (_tray?.IsRegistered != true
            || e.Args.Any(arg => string.Equals(arg, "--show", StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowDeviceWindow();
        }

        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activate!, (_, _) =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        Log("Opening devices in the existing instance");
                        window.ShowFlyout();
                    }
                });
            }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        try
        {
            if (_pairedDevices?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _pairedDevices.Stop();
        }
        catch (Exception ex)
        {
            Log("Bluetooth pairing watcher cleanup failed: " + ex.Message);
        }

        try
        {
            _tray?.Dispose();
        }
        finally
        {
            _activate?.Dispose();
            _instance?.ReleaseMutex();
            _instance?.Dispose();
            base.OnExit(e);
        }
    }

    private bool TryStartPrimaryInstance()
    {
        var scope = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "SoundBridge.Activate", scope);
        var instance = new Mutex(true, "SoundBridge.Instance", scope, out bool first);
        if (!first)
        {
            try { first = instance.WaitOne(0); }
            catch (AbandonedMutexException) { first = true; }
        }

        if (first)
        {
            _instance = instance;
            return true;
        }

        instance.Dispose();
        _activate.Set();
        return false;
    }

    private void StartPairingLog()
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            watcher.Added += (_, device) => Log($"Bluetooth paired device available: {device.Name}");
            watcher.Removed += (_, _) => Log("A Bluetooth device left the paired device list");
            watcher.EnumerationCompleted += (_, _) => Log("Initial Bluetooth pairing scan complete");
            watcher.Start();
            _pairedDevices = watcher;
        }
        catch (Exception ex)
        {
            Log($"Bluetooth pairing watcher failed: {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("DispatcherUnhandled: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "声桥出错", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    internal static void Log(string message)
    {
        lock (LogGate)
            AppendLog(message);
    }

    private static void AppendLog(string message)
    {
        string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoundBridge");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "log.txt"), entry);
            }
            catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine(entry);
            }
        }
    }
}
