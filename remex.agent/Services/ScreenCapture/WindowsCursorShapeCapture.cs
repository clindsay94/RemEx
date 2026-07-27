using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Remex.Agent.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal static class WindowsCursorShapeCapture
{
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;
    private const int SmCxCursor = 13;
    private const int SmCyCursor = 14;

    public static bool TryCaptureCurrentShape(out CursorShapeSnapshot? snapshot)
    {
        snapshot = null;

        var cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.flags & CursorShowing) == 0 ||
            cursorInfo.hCursor == IntPtr.Zero)
        {
            return false;
        }

        if (!GetIconInfo(cursorInfo.hCursor, out var iconInfo))
        {
            return false;
        }

        try
        {
            var width = GetCursorMetric(iconInfo.hbmColor, SmCxCursor);
            var height = GetCursorMetric(iconInfo.hbmColor, SmCyCursor);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            using var blackBitmap = RenderCursorBitmap(cursorInfo.hCursor, width, height, iconInfo.xHotspot, iconInfo.yHotspot, Color.Black);
            using var whiteBitmap = RenderCursorBitmap(cursorInfo.hCursor, width, height, iconInfo.xHotspot, iconInfo.yHotspot, Color.White);
            var blackBytes = ReadBitmapBytes(blackBitmap);
            var whiteBytes = ReadBitmapBytes(whiteBitmap);
            var shapeBytes = ComposeAlpha(blackBytes, whiteBytes);

            snapshot = new CursorShapeSnapshot(
                cursorInfo.hCursor,
                width,
                height,
                iconInfo.xHotspot,
                iconInfo.yHotspot,
                shapeBytes);
            return true;
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmColor);
            }

            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    /// <summary>
    /// Cheap poll of the current cursor's handle, without the bitmap-rendering cost of
    /// <see cref="TryCaptureCurrentShape"/>. Used to detect shape *changes* fast (every 90Hz
    /// position tick) without paying the full capture cost on every tick — the caller only
    /// invokes the real capture when the handle actually differs from the last seen one.
    /// </summary>
    public static bool TryGetCursorHandle(out IntPtr handle)
    {
        var cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.flags & CursorShowing) == 0 ||
            cursorInfo.hCursor == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            return false;
        }

        handle = cursorInfo.hCursor;
        return true;
    }

    private static int GetCursorMetric(IntPtr colorBitmapHandle, int fallbackMetric)
    {
        if (colorBitmapHandle != IntPtr.Zero &&
            GetObject(colorBitmapHandle, Marshal.SizeOf<BITMAP>(), out BITMAP bitmap) != 0)
        {
            return fallbackMetric == SmCxCursor ? bitmap.bmWidth : bitmap.bmHeight;
        }

        return GetSystemMetrics(fallbackMetric);
    }

    private static Bitmap RenderCursorBitmap(IntPtr cursorHandle, int width, int height, int hotspotX, int hotspotY, Color background)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(background);
        var hdc = graphics.GetHdc();
        try
        {
            DrawIconEx(hdc, -hotspotX, -hotspotY, cursorHandle, 0, 0, 0, IntPtr.Zero, DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static byte[] ReadBitmapBytes(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte[] ComposeAlpha(byte[] blackBytes, byte[] whiteBytes)
    {
        var composed = new byte[blackBytes.Length];
        for (var index = 0; index < blackBytes.Length; index += 4)
        {
            var blueBlack = blackBytes[index];
            var greenBlack = blackBytes[index + 1];
            var redBlack = blackBytes[index + 2];

            var blueWhite = whiteBytes[index];
            var greenWhite = whiteBytes[index + 1];
            var redWhite = whiteBytes[index + 2];

            var alpha = 255 - Math.Max(
                Math.Max(Math.Abs(redWhite - redBlack), Math.Abs(greenWhite - greenBlack)),
                Math.Abs(blueWhite - blueBlack));
            alpha = Math.Clamp(alpha, 0, 255);

            composed[index] = blueBlack;
            composed[index + 1] = greenBlack;
            composed[index + 2] = redBlack;
            composed[index + 3] = (byte)alpha;
        }

        return composed;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern int DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        int istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        int diFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }
}

internal sealed record CursorShapeSnapshot(
    IntPtr CursorHandle,
    int Width,
    int Height,
    int HotspotX,
    int HotspotY,
    byte[] ShapeBytes);
