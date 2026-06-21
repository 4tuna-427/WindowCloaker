using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Principal;
using Wpf.Ui.Controls;

namespace WindowCloaker
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            CheckAdminStatus();
            LoadWindows();
            SetupNotifyIcon();
            StateChanged += MainWindow_StateChanged;
            SetupForegroundHook();
        }

        private void CheckAdminStatus()
        {
            if (IsAdministrator())
            {
                Title = "Window Cloaker (管理者)";
                RestartAsAdminButton.Visibility = Visibility.Collapsed;
            }
        }

        public static bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
        {
            var exeName = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exeName)) return;

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(exeName)
                {
                    Verb = "runas",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
                System.Windows.Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // UACプロンプトでキャンセルされた場合は何もしない
            }
        }

        private System.Windows.Forms.NotifyIcon _notifyIcon;

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            var iconPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
            {
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(iconPath);
            }
            _notifyIcon.Text = "Window Cloaker";
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("開く");
            openItem.Click += (s, e) => ShowMainWindow();
            contextMenu.Items.Add(openItem);

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("終了");
            exitItem.Click += (s, e) => System.Windows.Application.Current.Shutdown();
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
            }
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = true;
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            if (_hWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_hWinEventHook);
                _hWinEventHook = IntPtr.Zero;
            }

            // 監視対象のアプリを元の状態に戻す
            var windows = WindowListView.ItemsSource as IEnumerable<WindowInfo>;
            if (windows != null)
            {
                foreach (var w in windows)
                {
                    if (w.IsManaged)
                    {
                        UncloakWindow(w);
                    }
                }
            }

            base.OnClosed(e);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadWindows();
        }

        private void WindowListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (WindowListView.SelectedItem is WindowInfo selectedWindow)
            {
                if (IsIconic(selectedWindow.Handle))
                {
                    ShowWindow(selectedWindow.Handle, SW_RESTORE);
                }
                SetForegroundWindow(selectedWindow.Handle);
            }
        }

        private void LoadWindows()
        {
            var existingWindows = WindowListView.ItemsSource as List<WindowInfo> ?? new List<WindowInfo>();
            var existingMap = new Dictionary<IntPtr, WindowInfo>();
            foreach (var w in existingWindows)
            {
                existingMap[w.Handle] = w;
            }

            var newWindows = new List<WindowInfo>();

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    // サイズ0のウィンドウを除外するフィルタ
                    GetWindowRect(hWnd, out RECT rect);
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    
                    if (width <= 0 || height <= 0)
                    {
                        return true; // スキップ
                    }

                    // シェルウィンドウ (Program Manager 等) を除外
                    if (hWnd == GetShellWindow())
                    {
                        return true;
                    }

                    // Windows 10/11のバックグラウンドアプリ（クロークされたウィンドウ）を除外
                    DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, Marshal.SizeOf(typeof(int)));
                    if (cloaked != 0)
                    {
                        return true;
                    }

                    // ツールウィンドウを除外
                    int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    {
                        return true;
                    }

                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(hWnd, title, title.Capacity);

                    if (title.Length > 0)
                    {
                        if (existingMap.TryGetValue(hWnd, out var existing))
                        {
                            existing.Title = title.ToString();
                            existing.Icon = GetAppIcon(hWnd); // Update icon in case it changed
                            newWindows.Add(existing);
                        }
                        else
                        {
                            var info = new WindowInfo { Handle = hWnd, Title = title.ToString(), Icon = GetAppIcon(hWnd) };
                            info.PropertyChanged += WindowInfo_PropertyChanged;
                            newWindows.Add(info);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            WindowListView.ItemsSource = newWindows;
        }

        private void WindowInfo_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowInfo.IsManaged) && sender is WindowInfo windowInfo)
            {
                if (windowInfo.IsManaged)
                {
                    if (GetForegroundWindow() == windowInfo.Handle)
                    {
                        if (!windowInfo.OriginalExStyle.HasValue)
                        {
                            windowInfo.OriginalExStyle = GetWindowLong(windowInfo.Handle, GWL_EXSTYLE);
                        }
                        UncloakWindow(windowInfo);
                    }
                    else
                    {
                        CloakWindow(windowInfo);
                    }
                }
                else
                {
                    UncloakWindow(windowInfo);
                    windowInfo.OriginalExStyle = null;
                }
            }
        }

        private void CloakWindow(WindowInfo windowInfo)
        {
            if (windowInfo.IsCurrentlyCloaked) return;

            IntPtr hwnd = windowInfo.Handle;
            
            // 現在の拡張スタイルを取得して保存
            int currentExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (!windowInfo.OriginalExStyle.HasValue)
            {
                windowInfo.OriginalExStyle = currentExStyle;
            }

            // WS_EX_LAYERED と WS_EX_TRANSPARENT を付与
            int newExStyle = windowInfo.OriginalExStyle.Value | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, newExStyle);

            // 完全透明化
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

            windowInfo.IsCurrentlyCloaked = true;
        }

        private void UncloakWindow(WindowInfo windowInfo)
        {
            if (!windowInfo.IsCurrentlyCloaked) return;

            IntPtr hwnd = windowInfo.Handle;

            // まず不透明に戻す（アルファ255）
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);

            // 元のスタイルに戻す
            if (windowInfo.OriginalExStyle.HasValue)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, windowInfo.OriginalExStyle.Value);
            }

            windowInfo.IsCurrentlyCloaked = false;
        }

        public class WindowInfo : INotifyPropertyChanged
        {
            public IntPtr Handle { get; set; }
            
            private string _title;
            public string Title
            {
                get => _title;
                set
                {
                    if (_title != value)
                    {
                        _title = value;
                        OnPropertyChanged(nameof(Title));
                    }
                }
            }

            private ImageSource _icon;
            public ImageSource Icon
            {
                get => _icon;
                set
                {
                    if (_icon != value)
                    {
                        _icon = value;
                        OnPropertyChanged(nameof(Icon));
                    }
                }
            }

            private bool _isManaged;
            public bool IsManaged
            {
                get => _isManaged;
                set
                {
                    if (_isManaged != value)
                    {
                        _isManaged = value;
                        OnPropertyChanged(nameof(IsManaged));
                    }
                }
            }

            public int? OriginalExStyle { get; set; }
            public bool IsCurrentlyCloaked { get; set; }

            public event PropertyChangedEventHandler? PropertyChanged;
            protected virtual void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private static IntPtr SendMessageSafe(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (SendMessageTimeout(hWnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 100, out IntPtr result) == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            return result;
        }

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLong")]
        private static extern IntPtr GetClassLong32(IntPtr hWnd, int nIndex);

        private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetClassLongPtr64(hWnd, nIndex);
            else
                return GetClassLong32(hWnd, nIndex);
        }

        private const uint WM_GETICON = 0x007F;
        private const int ICON_SMALL2 = 2;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int GCL_HICON = -14;
        private const int GCL_HICONSM = -34;

        private ImageSource GetAppIcon(IntPtr hwnd)
        {
            IntPtr hIcon = SendMessageSafe(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessageSafe(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessageSafe(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hwnd, GCL_HICON);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hwnd, GCL_HICONSM);

            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // --- 透明化・クリック透過用 API ---
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int LWA_ALPHA = 0x00000002;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        private const int DWMWA_CLOAKED = 14;

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // --- フォアグラウンド変更検知 ---
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint EVENT_SYSTEM_FOREGROUND = 3;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private static WinEventDelegate _winEventDelegate;
        private IntPtr _hWinEventHook;

        private void SetupForegroundHook()
        {
            _winEventDelegate = new WinEventDelegate(WinEventProc);
            _hWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (eventType == EVENT_SYSTEM_FOREGROUND)
                {
                    UpdateCloakStates(hwnd);
                }
            }
            catch
            {
                // 例外がOS側に伝播して強制終了するのを防ぐ
            }
        }

        private void UpdateCloakStates(IntPtr foregroundHwnd)
        {
            var windows = WindowListView.ItemsSource as IEnumerable<WindowInfo>;
            if (windows != null)
            {
                foreach (var w in windows)
                {
                    if (w.IsManaged)
                    {
                        if (w.Handle == foregroundHwnd)
                        {
                            UncloakWindow(w);
                        }
                        else
                        {
                            CloakWindow(w);
                        }
                    }
                }
            }
        }
    }
}