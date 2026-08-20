using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexPresence;

/// <summary>
/// Native notification-area icon for the WinUI shell. WinUI does not expose a
/// tray API, so this class owns a small hidden Win32 window and a native popup
/// menu without taking a dependency on Windows Forms.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WmApp + 0x71;
    private const uint WmApp = 0x8000;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotificationIconVersion4 = 4;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;

    private const uint NiifInfo = 0x00000001;
    private const uint NiifError = 0x00000003;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint IdiApplication = 32512;

    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfGrayed = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNoNotify = 0x0080;

    private const uint OpenCommand = 1001;
    private const uint ToggleCommand = 1002;
    private const uint SettingsCommand = 1003;
    private const uint DiagnosticsCommand = 1004;
    private const uint UpdateCommand = 1005;
    private const uint RestartCommand = 1006;
    private const uint ExitCommand = 1007;

    private readonly string windowClassName = $"CodexPresence.Tray.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly WindowProcedure windowProcedure;
    private readonly nint moduleHandle;
    private readonly uint taskbarCreatedMessage;

    private nint windowHandle;
    private nint iconHandle;
    private bool ownsIcon;
    private bool iconAdded;
    private bool classRegistered;
    private bool disposed;

    private string toolTip = "Codex Presence — starting";
    private string status = "Starting…";
    private string activity = "Waiting for activity";
    private string toggleText = "Pause presence";

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DiagnosticsRequested;
    public event EventHandler? UpdateRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The notification-area icon requires Windows.");

        windowProcedure = WindowProc;
        moduleHandle = GetModuleHandleW(null);
        if (moduleHandle == 0) throw LastWin32Error("Could not get the application module handle.");

        try
        {
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = windowProcedure,
                Instance = moduleHandle,
                ClassName = windowClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
                throw LastWin32Error("Could not register the tray window class.");
            classRegistered = true;

            windowHandle = CreateWindowExW(
                WsExToolWindow | WsExNoActivate,
                windowClassName,
                "Codex Presence tray host",
                WsPopup,
                0,
                0,
                0,
                0,
                0,
                0,
                moduleHandle,
                0);
            if (windowHandle == 0) throw LastWin32Error("Could not create the tray window.");

            iconHandle = LoadApplicationIcon(out ownsIcon);
            taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            AddIcon(throwOnFailure: true);
        }
        catch
        {
            ReleaseNativeResources();
            disposed = true;
            throw;
        }
    }

    /// <summary>Updates both the shell tooltip and the read-only menu rows.</summary>
    public void UpdateStatus(string toolTip, string status, string activity, bool presenceEnabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        this.status = NormalizeMenuText(status, "Service unavailable");
        this.activity = NormalizeMenuText(activity, "Waiting for activity");
        toggleText = presenceEnabled ? "Pause presence" : "Resume presence";

        var normalizedTip = Truncate(string.IsNullOrWhiteSpace(toolTip) ? "Codex Presence" : toolTip.Trim(), 127);
        if (string.Equals(this.toolTip, normalizedTip, StringComparison.Ordinal)) return;

        this.toolTip = normalizedTip;
        if (!iconAdded) return;

        var data = CreateNotificationData(NifTip | NifShowTip);
        _ = Shell_NotifyIcon(NimModify, ref data);
    }

    public void ShowBalloon(string title, string message, bool isError = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!iconAdded) return;

        var data = CreateNotificationData(NifInfo);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = isError ? NiifError : NiifInfo;
        _ = Shell_NotifyIcon(NimModify, ref data);
    }

    private void AddIcon(bool throwOnFailure)
    {
        var data = CreateNotificationData(NifMessage | NifIcon | NifTip | NifShowTip);
        iconAdded = Shell_NotifyIcon(NimAdd, ref data);
        if (!iconAdded)
        {
            if (throwOnFailure) throw LastWin32Error("Could not add the notification-area icon.");
            return;
        }

        data.Flags = 0;
        data.TimeoutOrVersion = NotificationIconVersion4;
        _ = Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotificationIconData CreateNotificationData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotificationIconData>(),
        WindowHandle = windowHandle,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        IconHandle = iconHandle,
        Tip = toolTip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return WindowProcCore(hwnd, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // Exceptions must never escape a reverse P/Invoke callback.
            Debug.WriteLine($"Codex Presence tray callback failed: {error}");
            return message == CallbackMessage ? 0 : DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private nint WindowProcCore(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            // Explorer owns notification-area state. Re-register after it is restarted.
            iconAdded = false;
            AddIcon(throwOnFailure: false);
            return 0;
        }

        if (message != CallbackMessage) return DefWindowProcW(hwnd, message, wParam, lParam);

        var notification = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
        switch (notification)
        {
            case WmContextMenu:
            case WmRButtonUp:
                ShowContextMenu();
                break;
            case NinSelect:
            case NinKeySelect:
            case WmLButtonDoubleClick:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
        return 0;
    }

    private void ShowContextMenu()
    {
        if (disposed || windowHandle == 0) return;

        var menu = CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            Append(menu, OpenCommand, "Open Codex Presence");
            _ = SetMenuDefaultItem(menu, OpenCommand, false);
            AppendSeparator(menu);
            AppendDisabled(menu, status);
            AppendDisabled(menu, activity);
            AppendSeparator(menu);
            Append(menu, ToggleCommand, toggleText);
            Append(menu, SettingsCommand, "Settings…");
            Append(menu, DiagnosticsCommand, "Run diagnostics…");
            Append(menu, UpdateCommand, "Check for updates…");
            Append(menu, RestartCommand, "Restart service");
            AppendSeparator(menu);
            Append(menu, ExitCommand, "Exit");

            if (!GetCursorPos(out var cursor)) return;
            _ = SetForegroundWindow(windowHandle);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCommand | TpmNoNotify,
                cursor.X,
                cursor.Y,
                0,
                windowHandle,
                0);
            DispatchCommand(command);

            // Required by the notification-area menu contract so it dismisses
            // reliably when the user clicks another window.
            _ = PostMessageW(windowHandle, WmNull, 0, 0);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void DispatchCommand(uint command)
    {
        var handler = command switch
        {
            OpenCommand => OpenRequested,
            ToggleCommand => ToggleRequested,
            SettingsCommand => SettingsRequested,
            DiagnosticsCommand => DiagnosticsRequested,
            UpdateCommand => UpdateRequested,
            RestartCommand => RestartRequested,
            ExitCommand => ExitRequested,
            _ => null,
        };
        handler?.Invoke(this, EventArgs.Empty);
    }

    private static void Append(nint menu, uint id, string text) =>
        _ = AppendMenuW(menu, MfString, id, EscapeMenuText(text));

    private static void AppendDisabled(nint menu, string text) =>
        _ = AppendMenuW(menu, MfString | MfDisabled | MfGrayed, 0, EscapeMenuText(text));

    private static void AppendSeparator(nint menu) => _ = AppendMenuW(menu, MfSeparator, 0, null);

    private static string EscapeMenuText(string text) => Truncate(text.Replace("&", "&&", StringComparison.Ordinal), 96);

    private static string NormalizeMenuText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= maximumLength) return value;

        var length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    private static nint LoadApplicationIcon(out bool ownsHandle)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "codex-presence.ico"),
            Path.Combine(AppContext.BaseDirectory, "assets", "codex-presence.ico"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "assets", "codex-presence.ico")),
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            var loaded = LoadImageW(0, candidate, ImageIcon, 16, 16, LrLoadFromFile);
            if (loaded == 0) continue;
            ownsHandle = true;
            return loaded;
        }

        ownsHandle = false;
        var fallback = LoadIconW(0, new nint(IdiApplication));
        if (fallback == 0) throw LastWin32Error("Could not load an icon for the notification area.");
        return fallback;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseNativeResources();
        GC.SuppressFinalize(this);
    }

    private void ReleaseNativeResources()
    {
        if (iconAdded && windowHandle != 0)
        {
            var data = CreateNotificationData(0);
            _ = Shell_NotifyIcon(NimDelete, ref data);
            iconAdded = false;
        }

        if (windowHandle != 0)
        {
            _ = DestroyWindow(windowHandle);
            windowHandle = 0;
        }

        if (ownsIcon && iconHandle != 0)
        {
            _ = DestroyIcon(iconHandle);
            iconHandle = 0;
            ownsIcon = false;
        }

        if (classRegistered)
        {
            _ = UnregisterClassW(windowClassName, moduleHandle);
            classRegistered = false;
        }
    }

    private static Win32Exception LastWin32Error(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WindowProcedure WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotificationIconData data);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetMenuDefaultItem(nint menu, uint item, bool byPosition);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint owner,
        nint rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
