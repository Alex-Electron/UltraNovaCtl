using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace UltraNovaCtl.Core;

/// <summary>
/// Tells us when the instrument is plugged in or pulled out.
///
/// Windows announces device interfaces coming and going through WM_DEVICECHANGE, which
/// needs a window to deliver to. So this runs an invisible top-level window on its own thread,
/// registers it for notifications about the audio device-interface class the instrument's
/// KS filter belongs to, and raises an event per arrival and removal whose path names a
/// Novation device. Hidi64.dll does exactly this with a window class it calls
/// NovationHIDINotify; the approach is standard and this is our own copy of it.
///
/// Events are raised on the watcher's thread. Consumers marshal.
///
/// This is only the announcement. What to do about it - close pins on removal, reopen them
/// on arrival after the driver has settled - belongs to whoever owns the connection.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceWatcher : IDisposable
{
    /// <summary>KSCATEGORY_AUDIO, the interface class the instrument's filter is registered under.</summary>
    static readonly Guid KsCategoryAudio = new("6994ad04-93ef-11d0-a3cc-00a0c9223196");

    const string Vendor = "vid_1235";
    const string WindowClass = "UltraNovaCtl.DeviceWatch";

    const uint WM_DEVICECHANGE = 0x0219;
    const uint WM_CLOSE = 0x0010;
    const int DBT_DEVICEARRIVAL = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    Thread _thread;
    IntPtr _hwnd;
    IntPtr _notification;
    WndProc _proc;                       // kept alive; the window holds only an unmanaged pointer
    readonly ManualResetEventSlim _ready = new(false);
    string _failure;
    bool _disposed;

    /// <summary>A Novation device interface appeared. The argument is its device path.</summary>
    public event EventHandler<string> Arrived;

    /// <summary>A Novation device interface went away. The argument is its device path.</summary>
    public event EventHandler<string> Removed;

    /// <summary>True while the window exists and notifications are registered.</summary>
    public bool IsRunning => _hwnd != IntPtr.Zero && _notification != IntPtr.Zero;

    /// <summary>Why <see cref="Start"/> failed, when it did.</summary>
    public string Failure => _failure;

    /// <summary>
    /// Is this the device we care about? Interface paths look like
    /// \\?\usb#vid_1235&amp;pid_0011#...#{6994ad04-...}\global; the vendor id is what
    /// identifies Novation, and the check is case-insensitive because the shell is.
    /// </summary>
    public static bool IsNovationPath(string devicePath)
        => devicePath != null && devicePath.IndexOf(Vendor, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Bring the window up and register. Returns false, with <see cref="Failure"/> set, if
    /// Windows refused - the watcher is then inert and safe to dispose.
    /// </summary>
    public bool Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeviceWatcher));
        if (_thread != null) return IsRunning;

        _thread = new Thread(Pump) { IsBackground = true, Name = "UltraNova device watch" };
        _thread.Start();
        _ready.Wait();
        return IsRunning;
    }

    void Pump()
    {
        try
        {
            _proc = WindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = WindowClass,
            };
            // Registering twice in one process is an error we do not care about.
            RegisterClassEx(ref wc);

            // An ordinary invisible top-level window, not a message-only one: message-only
            // windows do not receive broadcast messages, and WM_DEVICECHANGE is one. Hidi
            // creates exactly this kind of window, and it is the kind that is known to work.
            _hwnd = CreateWindowEx(0, WindowClass, "", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _failure = "window: error " + Marshal.GetLastWin32Error();
                return;
            }

            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = (uint)Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = KsCategoryAudio,
            };
            IntPtr buf = Marshal.AllocHGlobal((int)filter.dbcc_size);
            try
            {
                Marshal.StructureToPtr(filter, buf, false);
                _notification = RegisterDeviceNotification(_hwnd, buf, DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (_notification == IntPtr.Zero)
            {
                _failure = "notification: error " + Marshal.GetLastWin32Error();
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return;
            }
        }
        finally { _ready.Set(); }

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int what = wParam.ToInt32();
            if ((what == DBT_DEVICEARRIVAL || what == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero)
            {
                var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (hdr.dbch_devicetype == DBT_DEVTYP_DEVICEINTERFACE)
                {
                    // The name follows the fixed part of the structure and is NUL-terminated.
                    int nameOffset = Marshal.OffsetOf<DEV_BROADCAST_DEVICEINTERFACE>("dbcc_name").ToInt32();
                    string path = Marshal.PtrToStringUni(lParam + nameOffset) ?? "";
                    if (IsNovationPath(path))
                    {
                        if (what == DBT_DEVICEARRIVAL) Arrived?.Invoke(this, path);
                        else Removed?.Invoke(this, path);
                    }
                }
            }
            return new IntPtr(1);                // TRUE: we handled it
        }
        if (msg == WM_CLOSE)
        {
            DestroyWindow(hwnd);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_notification != IntPtr.Zero) { UnregisterDeviceNotification(_notification); _notification = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero)
        {
            // Ask the window thread to shut its own window and leave its loop.
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(1500);
            _hwnd = IntPtr.Zero;
        }
        _ready.Dispose();
    }

    // ---- interop -----------------------------------------------------------

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public uint dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;               // first character of a variable-length string
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string lpModuleName);
}
