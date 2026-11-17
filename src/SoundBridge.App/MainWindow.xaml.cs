using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoundBridge.Core;
using Forms = System.Windows.Forms;

namespace SoundBridge.App;

public partial class MainWindow : Window
{
    private readonly AudioPlaybackReceiver _receiver;
    private readonly CancellationTokenSource _closing = new();
    private bool _shuttingDown;
    private bool _canClose;
    private bool _refreshing;
    private bool _isDeviceWindow;
    private System.Drawing.Rectangle _trayWorkArea;

    public ObservableCollection<DeviceRow> Devices { get; } = new();

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    internal MainWindow(AppSettings settings)
    {
        _receiver = new AudioPlaybackReceiver(App.Log, () => settings.AutoWakeEnabled);
        InitializeComponent();
        DataContext = this;

        _receiver.ConnectionChanged += OnConnectionChanged;
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_isDeviceWindow) PositionNearTray();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
    }

    public void ShowFlyout()
    {
        if (_isDeviceWindow)
        {
            ShowDeviceWindow();
            return;
        }

        if (IsVisible)
        {
            Activate();
            return;
        }

        _trayWorkArea = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        Opacity = 0;
        Show();
        UpdateLayout();
        PositionNearTray();
        Opacity = 1;
        Activate();

        _ = RefreshAsync();
    }

    public void HideFlyout()
    {
        if (_isDeviceWindow || !IsVisible) return;
        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }

    public void ShowDeviceWindow()
    {
        if (!_isDeviceWindow)
        {
            _isDeviceWindow = true;
            ShowInTaskbar = true;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            CloseDeviceWindowButton.Visibility = Visibility.Visible;
        }

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible)
        {
            Show();
            _ = RefreshAsync();
        }
        Activate();
    }

    private void OnCloseDeviceWindowClick(object sender, RoutedEventArgs e) => Close();

    private void PositionNearTray()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        Left = _trayWorkArea.Right / scale - ActualWidth - 12;
        Top = _trayWorkArea.Bottom / scale - ActualHeight - 12;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _shuttingDown) return;
        _refreshing = true;

        RefreshButton.IsEnabled = false;
        ShowScanNotice(Devices.Count == 0 ? "正在查找设备…" : null);
        try
        {
            IReadOnlyList<AudioDevice> devices = await _receiver.GetDevicesAsync(_closing.Token);
            if (_shuttingDown) return;

            var live = new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                DeviceRow row = Devices[i];
                if (!live.Contains(row.Id) && !row.Busy
                    && _receiver.GetConnectionState(row.Id) == ConnectionState.Disconnected)
                {
                    Devices.RemoveAt(i);
                }
            }

            foreach (AudioDevice device in devices)
            {
                DeviceRow? row = Devices.FirstOrDefault(
                    d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));

                if (row is null)
                    Devices.Add(new DeviceRow(device.Id, device.Name) { State = _receiver.GetConnectionState(device.Id) });
                else row.Name = device.Name;
            }

            ShowScanNotice(Devices.Count == 0 ? "请先在 Windows 蓝牙设置中配对设备。" : null);
        }
        catch (OperationCanceledException) when (_shuttingDown)
        {
        }
        catch (Exception ex)
        {
            ShowScanNotice("扫描失败：" + ex.Message);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowScanNotice(string? message)
    {
        ScanNotice.Text = message;
        ScanNotice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_shuttingDown || sender is not Button { Tag: DeviceRow row } || !row.CanToggle) return;

        ConnectionState state = _receiver.GetConnectionState(row.Id);
        if (state is ConnectionState.Connecting or ConnectionState.Disconnecting) return;
        row.Busy = true;
        try
        {
            if (state == ConnectionState.Connected)
            {
                await _receiver.DisconnectAsync(row.Id, _closing.Token);
            }
            else
            {
                await _receiver.ConnectAsync(row.Id, _closing.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the app or the connection can cancel an in-flight operation.
        }
        catch (Exception ex)
        {
            App.Log("Device operation failed: " + ex);
        }
        finally
        {
            row.Busy = false;
        }
    }

    private void OnConnectionChanged(ConnectionUpdate update)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_shuttingDown) return;
            DeviceRow? row = Devices.FirstOrDefault(
                d => string.Equals(d.Id, update.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (row is null) return;

            row.State = update.State;
            row.Error = update.Error;
        });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_canClose || e.Cancel) return;
        e.Cancel = true;
        _ = Dispatcher.InvokeAsync(RequestExitAsync);
    }

    internal async Task RequestExitAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _receiver.ConnectionChanged -= OnConnectionChanged;
        try
        {
            _closing.Cancel();
            await _receiver.DisposeAsync();
        }
        catch (Exception ex)
        {
            App.Log("Audio connection cleanup failed: " + ex.Message);
        }
        finally
        {
            _closing.Dispose();
            _canClose = true;
            Close();
        }
    }
}

public sealed class DeviceRow : INotifyPropertyChanged
{
    private string _name;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _busy;
    private string? _error;

    public DeviceRow(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public ConnectionState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StateText));
            Raise(nameof(ActionText));
            Raise(nameof(CanToggle));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (!Set(ref _error, value)) return;
            Raise(nameof(StateText));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(ActionText));
            Raise(nameof(CanToggle));
        }
    }

    public string StateText => _state switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connecting => "正在连接…",
        ConnectionState.Connected => "已连接",
        ConnectionState.Disconnecting => "正在断开…",
        ConnectionState.Failed => string.IsNullOrWhiteSpace(_error) ? "操作失败" : _error,
        _ => _state.ToString()
    };

    public bool CanToggle => !_busy && _state is not (ConnectionState.Connecting or ConnectionState.Disconnecting);

    public string ActionText => !CanToggle
        ? "…"
        : _state is ConnectionState.Connected or ConnectionState.Connecting ? "断开" : "连接";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
