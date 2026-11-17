using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace SoundBridge.App;

internal sealed class TrayService : IDisposable
{
    private const uint AddIcon = 0;
    private const uint DeleteIcon = 2;
    private const int TrayCallback = 0x8001;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;

    private readonly MainWindow _window;
    private readonly HwndSource _messageWindow;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly uint _taskbarCreated;
    private bool _disposed;

    public bool IsRegistered { get; private set; }

    public TrayService(MainWindow window, AppSettings settings)
    {
        _window = window;
        _icon = LoadIcon();
        _menu = new Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(248, 252, 255),
            ForeColor = Color.FromArgb(23, 45, 67),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new TrayMenuRenderer(),
            Padding = new Forms.Padding(5)
        };
        var showDevices = new Forms.ToolStripMenuItem("显示设备列表", null, (_, _) => _window.ShowFlyout());
        var autoWake = new Forms.ToolStripMenuItem("自动恢复声音")
        {
            Checked = settings.AutoWakeEnabled,
            ToolTipText = "连接后短暂静音一次，再恢复声音"
        };
        autoWake.Click += (_, _) =>
        {
            try
            {
                settings.SetAutoWake(!autoWake.Checked);
                autoWake.Checked = settings.AutoWakeEnabled;
                App.Log($"Automatic audio recovery changed: {(autoWake.Checked ? "on" : "off")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log("Could not save automatic audio recovery preference: " + ex.Message);
                System.Windows.MessageBox.Show("无法保存自动恢复声音设置。", "声桥");
            }
        };
        var startup = new Forms.ToolStripMenuItem("开机自启动")
        {
            Checked = settings.StartWithWindows,
            ToolTipText = "登录 Windows 后自动启动声桥"
        };
        startup.Click += (_, _) =>
        {
            try
            {
                settings.SetStartWithWindows(!startup.Checked);
                startup.Checked = settings.StartWithWindows;
                App.Log($"Start with Windows changed: {(startup.Checked ? "on" : "off")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                App.Log("Could not save Windows startup preference: " + ex.Message);
                System.Windows.MessageBox.Show("无法更改开机自启动设置。\n" + ex.Message, "声桥");
            }
        };

        _menu.Items.Add(showDevices);
        _menu.Items.Add(autoWake);
        _menu.Items.Add(startup);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("退出声桥", null, async (_, _) => await _window.RequestExitAsync());
        _menu.Items.Add(exit);
        foreach (Forms.ToolStripMenuItem item in new[] { showDevices, autoWake, startup, exit })
            item.Padding = new Forms.Padding(4, 4, 8, 4);

        HwndSource? messageWindow = null;
        try
        {
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            messageWindow = new HwndSource(new HwndSourceParameters("SoundBridge.Tray")
            {
                WindowStyle = unchecked((int)0x80000000) // WS_POPUP, hidden top-level window
            });
            _messageWindow = messageWindow;
            _messageWindow.AddHook(WindowProc);
            RegisterIcon();
        }
        catch
        {
            messageWindow?.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            throw;
        }
    }

    private void RegisterIcon()
    {
        NotifyIconData data = CreateIconData();
        IsRegistered = ShellNotifyIcon(AddIcon, ref data);
        int error = Marshal.GetLastPInvokeError();
        App.Log(IsRegistered
            ? "Tray: NIM_ADD succeeded"
            : $"Tray: NIM_ADD failed (Win32 error={error})");
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            RegisterIcon();
            if (!IsRegistered) _window.ShowDeviceWindow();
        }
        else if (message == TrayCallback && wParam == new IntPtr(1))
        {
            switch (lParam.ToInt32())
            {
                case LeftButtonUp:
                    ToggleFlyout();
                    break;
                case RightButtonUp:
                    SetForegroundWindow(hwnd);
                    _menu.Show(Forms.Cursor.Position);
                    break;
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleFlyout()
    {
        if (_window.IsVisible)
        {
            _window.HideFlyout();
            if (_window.IsVisible) _window.ShowDeviceWindow();
            return;
        }

        // A click on the icon can first deactivate an open flyout.
        if (DateTime.UtcNow - _window.LastHiddenUtc < TimeSpan.FromMilliseconds(250)) return;
        _window.ShowFlyout();
    }

    private static Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        try
        {
            return new Icon(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            App.Log($"Tray: could not load App.ico: {ex.Message}");
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRegistered)
        {
            NotifyIconData data = CreateIconData();
            ShellNotifyIcon(DeleteIcon, ref data);
            IsRegistered = false;
        }

        _messageWindow.RemoveHook(WindowProc);
        _messageWindow.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _messageWindow.Handle,
        Id = 1,
        Flags = 0x01 | 0x02 | 0x04, // NIF_MESSAGE | NIF_ICON | NIF_TIP
        CallbackMessage = TrayCallback,
        Icon = _icon.Handle,
        Tip = "声桥",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private sealed class TrayMenuRenderer() : Forms.ToolStripProfessionalRenderer(new TrayMenuColors())
    {
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96;
            float x = 13 * scale;
            float y = e.Item.Height / 2f;
            using var pen = new Pen(Color.FromArgb(24, 94, 145), 1.7f * scale)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            var previousMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new PointF[]
            {
                new(x - 4 * scale, y),
                new(x - scale, y + 3 * scale),
                new(x + 5 * scale, y - 3 * scale)
            });
            e.Graphics.SmoothingMode = previousMode;
        }
    }

    private sealed class TrayMenuColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(248, 252, 255);
        private static readonly Color Border = Color.FromArgb(204, 221, 237);
        private static readonly Color Selection = Color.FromArgb(225, 242, 254);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => Selection;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
