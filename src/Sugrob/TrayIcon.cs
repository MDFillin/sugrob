using System.Runtime.InteropServices;
using static Sugrob.Native;

namespace Sugrob;

/// <summary>Notification-area icon with a procedurally drawn snowflake.</summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = WM_APP + 1;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _iconActive;
    private readonly IntPtr _iconPaused;
    private bool _added;
    private bool _paused;
    private string _tip = "Sugrob";

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        int size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        _iconActive = CreateSnowIcon(size, 0x8C, 0xC8, 0xFF);
        _iconPaused = CreateSnowIcon(size, 0x99, 0x99, 0x99);
    }

    public void Show() => Apply(_added ? NIM_MODIFY : NIM_ADD);

    /// <summary>Explorer restarted — the icon must be added again.</summary>
    public void Recreate() { _added = false; Apply(NIM_ADD); }

    public void Update(bool paused, string tip) { _paused = paused; _tip = tip; if (_added) Apply(NIM_MODIFY); }

    private void Apply(uint op)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _paused ? _iconPaused : _iconActive,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };
        if (Shell_NotifyIcon(op, ref data) && op == NIM_ADD) _added = true;
    }

    /// <summary>A notification bubble from the tray icon.</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = 0x10 /* NIF_INFO */,
            szTip = _tip,
            szInfo = text.Length > 255 ? text[..255] : text,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            dwInfoFlags = 1 /* NIIF_INFO */,
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static unsafe IntPtr CreateSnowIcon(int size, byte r, byte g, byte b)
    {
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bih, 0, out var bits, IntPtr.Zero, 0);
        var px = (byte*)bits;
        const int ss = 4; // supersampling for smooth edges
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float acc = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                float nx = (x + (sx + 0.5f) / ss) / size * 2f - 1f;
                float ny = (y + (sy + 0.5f) / ss) / size * 2f - 1f;
                acc += Gpu.SampleSprite(2, nx * 1.05f, ny * 1.05f, thickScale: 2.2f);
            }
            byte a = (byte)Math.Clamp(acc / (ss * ss) * 255f, 0, 255);
            int i = (y * size + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; // straight alpha
        }
        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        DestroyIcon(_iconActive);
        DestroyIcon(_iconPaused);
    }
}
