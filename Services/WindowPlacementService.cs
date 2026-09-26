using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using WinUIEx;

namespace TrayRadar.Services;

/// <summary>
/// Positions a popup window next to the tray icon, falling back to the taskbar edge or the
/// primary display's work area (pattern from github.com/TheJoeFin/Traydio).
/// </summary>
internal static class WindowPlacementService
{
    private const uint ABM_GETTASKBARPOS = 0x5;
    private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    private const int WindowMargin = 12;

    private static nint _trayIconWindowHandle;
    private static uint _trayIconId;

    /// <summary>
    /// Captures the hidden message window WinUIEx uses for the tray icon so the icon rectangle can
    /// be queried with Shell_NotifyIconGetRect. WinUIEx does not expose it, so this uses reflection
    /// and silently degrades to cursor/taskbar placement if the internals change.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(TrayIcon))]
    public static void SetTrayIcon(TrayIcon trayIcon)
    {
        _trayIconId = trayIcon.TrayIconId;
        try
        {
            FieldInfo? field = typeof(TrayIcon).GetField("_windowHandle", BindingFlags.Instance | BindingFlags.NonPublic);
            _trayIconWindowHandle = field?.GetValue(trayIcon) switch
            {
                nint handle => handle,
                object boxed => ReadHandle(boxed),
                _ => 0,
            };
        }
        catch
        {
            _trayIconWindowHandle = 0;
        }
    }

    /// <summary>Moves and resizes <paramref name="window"/> (logical size) next to the tray icon.</summary>
    public static void PositionNearTray(Window window, int width, int height)
    {
        bool haveIcon = TryGetTrayIconRect(out RECT iconRect);
        PointInt32 anchor;
        if (haveIcon)
        {
            anchor = new((iconRect.left + iconRect.right) / 2, (iconRect.top + iconRect.bottom) / 2);
        }
        else
        {
            PInvoke.GetCursorPos(out System.Drawing.Point cursor);
            anchor = new(cursor.X, cursor.Y);
        }

        DisplayArea displayArea = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Primary);
        RectInt32 work = displayArea.WorkArea;
        uint dpi = GetDpiForPoint(anchor);
        int w = ToPhysical(width, dpi);
        int h = ToPhysical(height, dpi);
        int margin = ToPhysical(WindowMargin, dpi);

        int x = anchor.X - (w / 2);
        int y = work.Y + work.Height - h - margin;

        if (TryGetTaskbarEdge(out RECT taskbar, out uint edge))
        {
            switch (edge)
            {
                case ABE_BOTTOM:
                    y = Math.Min(taskbar.top, work.Y + work.Height) - h - margin;
                    break;
                case ABE_TOP:
                    y = Math.Max(taskbar.bottom, work.Y) + margin;
                    break;
                case ABE_LEFT:
                    x = Math.Max(taskbar.right, work.X) + margin;
                    y = anchor.Y - h + margin;
                    break;
                case ABE_RIGHT:
                    x = Math.Min(taskbar.left, work.X + work.Width) - w - margin;
                    y = anchor.Y - h + margin;
                    break;
            }
        }

        x = Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.X + work.Width - w - margin));
        y = Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Y + work.Height - h - margin));
        window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    /// <summary>Centers <paramref name="window"/> (logical size) on the primary display.</summary>
    public static void CenterOnPrimary(Window window, int width, int height)
    {
        RectInt32 work = DisplayArea.Primary.WorkArea;
        uint dpi = GetDpiForPoint(new PointInt32(work.X + (work.Width / 2), work.Y + (work.Height / 2)));
        int w = ToPhysical(width, dpi);
        int h = ToPhysical(height, dpi);
        window.AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - w) / 2), work.Y + ((work.Height - h) / 2), w, h));
    }

    private static bool TryGetTrayIconRect(out RECT rect)
    {
        rect = default;
        if (_trayIconWindowHandle == 0)
        {
            return false;
        }

        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = (HWND)_trayIconWindowHandle,
            uID = _trayIconId,
        };
        // Returns S_OK on success; icons hidden in the overflow area report an empty rect.
        return PInvoke.Shell_NotifyIconGetRect(in id, out rect).Succeeded && rect.right > rect.left;
    }

    private static bool TryGetTaskbarEdge(out RECT rect, out uint edge)
    {
        APPBARDATA data = new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (PInvoke.SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0)
        {
            rect = default;
            edge = ABE_BOTTOM;
            return false;
        }

        rect = data.rc;
        edge = data.uEdge;
        return true;
    }

    private static uint GetDpiForPoint(PointInt32 point)
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : 96;
    }

    private static int ToPhysical(int logical, uint dpi) => (int)Math.Ceiling(logical * dpi / 96.0);

    private static nint ReadHandle(object boxed)
    {
        // WinUIEx stores its own CsWin32 HWND struct; read its single pointer-sized field.
        FieldInfo? valueField = boxed.GetType().GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return valueField?.GetValue(boxed) switch
        {
            nint n => n,
            _ => 0,
        };
    }
}
