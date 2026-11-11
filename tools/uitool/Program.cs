using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 验证用 UI 小工具（不参与主程序构建）：
//   uitool cap <out.bmp> [窗口关键字]
//   uitool click <x> <y> [wheelDelta]
//   uitool focus <窗口关键字> [top]
//   uitool rect <窗口关键字>
//   uitool list
internal static class Program
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int BI_RGB = 0;
    private const uint LEFTDOWN = 0x0002;
    private const uint LEFTUP = 0x0004;
    private const uint WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        if (args.Length == 0)
        {
            Console.WriteLine("usage: cap|click|focus|rect|list");
            return 2;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "cap": return Cap(args);
            case "click": return Click(args);
            case "focus": return Focus(args);
            case "rect": return Rect(args);
            case "list": return List();
            case "qs": return QuickSettings();
            case "move": return Move(args);
            default: Console.WriteLine("unknown command"); return 2;
        }
    }

    // 只移动鼠标（用于唤出自动隐藏的任务栏 / 触发托盘悬浮提示）。
    private static int Move(string[] args)
    {
        int x = int.Parse(args[1]);
        int y = int.Parse(args[2]);
        SetCursorPos(x, y);
        Console.WriteLine("moved " + x + "," + y);
        return 0;
    }

    // Win+A：打开/关闭 Windows 11 快速设置（媒体浮层就在它的顶部）。
    private static int QuickSettings()
    {
        const byte LWin = 0x5B, A = 0x41, KeyUp = 0x0002;
        keybd_event(LWin, 0, 0, UIntPtr.Zero);
        keybd_event(A, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);
        keybd_event(A, 0, KeyUp, UIntPtr.Zero);
        keybd_event(LWin, 0, KeyUp, UIntPtr.Zero);
        Console.WriteLine("Win+A sent");
        return 0;
    }

    private static int Cap(string[] args)
    {
        string outPath = args.Length > 1 ? args[1] : "cap.bmp";
        IntPtr hWnd = args.Length > 2 ? FindWindow(args[2]) : IntPtr.Zero;
        RECT rect;
        int width, height, srcX = 0, srcY = 0;
        if (hWnd != IntPtr.Zero)
        {
            GetWindowRect(hWnd, out rect);
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            srcX = rect.Left;
            srcY = rect.Top;
        }
        else
        {
            width = GetSystemMetrics(0);
            height = GetSystemMetrics(1);
        }
        if (width <= 0 || height <= 0) { Console.WriteLine("bad size"); return 4; }
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr old = SelectObject(memDc, hBitmap);
        bool drawn = hWnd != IntPtr.Zero && PrintWindow(hWnd, memDc, PW_RENDERFULLCONTENT);
        if (!drawn) drawn = BitBlt(memDc, 0, 0, width, height, screenDc, srcX, srcY, SRCCOPY | CAPTUREBLT);
        if (!drawn) { Console.WriteLine("capture failed"); return 5; }
        int stride = (width * 3 + 3) / 4 * 4;
        byte[] pixels = new byte[stride * height];
        BITMAPINFOHEADER info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = height,
            biPlanes = 1,
            biBitCount = 24,
            biCompression = BI_RGB,
            biSizeImage = pixels.Length
        };
        int lines = GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref info, 0);
        SelectObject(memDc, old);
        DeleteObject(hBitmap);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        if (lines == 0) { Console.WriteLine("GetDIBits failed"); return 6; }
        using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((byte)'B'); bw.Write((byte)'M');
            bw.Write(14 + 40 + pixels.Length); bw.Write(0); bw.Write(54);
            bw.Write(40); bw.Write(width); bw.Write(height);
            bw.Write((short)1); bw.Write((short)24);
            bw.Write(0); bw.Write(pixels.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
            bw.Write(pixels);
        }
        Console.WriteLine("saved " + outPath + " " + width + "x" + height);
        return 0;
    }

    private static int Click(string[] args)
    {
        int x = int.Parse(args[1]);
        int y = int.Parse(args[2]);
        SetCursorPos(x, y);
        Thread.Sleep(150);
        if (args.Length >= 4)
        {
            mouse_event(WHEEL, 0, 0, int.Parse(args[3]), IntPtr.Zero);
            Console.WriteLine("wheel");
            return 0;
        }
        mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
        Console.WriteLine("clicked " + x + "," + y);
        return 0;
    }

    private static int Focus(string[] args)
    {
        IntPtr hWnd = FindWindow(args[1]);
        if (hWnd == IntPtr.Zero) { Console.WriteLine("not found"); return 3; }
        if (IsIconic(hWnd)) ShowWindow(hWnd, 9);
        ShowWindow(hWnd, 5);
        if (args.Length >= 3 && args[2] == "top") SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        SetForegroundWindow(hWnd);
        Console.WriteLine("focused");
        return 0;
    }

    private static int Rect(string[] args)
    {
        IntPtr hWnd = FindWindow(args[1]);
        if (hWnd == IntPtr.Zero) { Console.WriteLine("not found"); return 3; }
        GetWindowRect(hWnd, out RECT r);
        StringBuilder t = new StringBuilder(256);
        GetWindowTextW(hWnd, t, t.Capacity);
        Console.WriteLine("title=[" + t + "] rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " size=" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
        return 0;
    }

    private static int List()
    {
        EnumWindows(delegate (IntPtr hWnd, IntPtr param)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            if (sb.Length > 0)
            {
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                Console.WriteLine(pid + " " + sb);
            }
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    private static IntPtr FindWindow(string keyword)
    {
        IntPtr found = Match(keyword, true);
        if (found == IntPtr.Zero) found = Match(keyword, false);
        return found;
    }

    private static IntPtr Match(string keyword, bool byProcess)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hWnd, IntPtr param)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (byProcess)
            {
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                string proc = "";
                try { proc = Process.GetProcessById(pid).ProcessName; } catch { }
                GetWindowRect(hWnd, out RECT r);
                bool big = (r.Right - r.Left) >= 400 && (r.Bottom - r.Top) >= 300;
                if (string.Equals(proc, keyword, StringComparison.OrdinalIgnoreCase) && title.Length > 0 && big)
                {
                    found = hWnd; return false;
                }
            }
            else if (title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = hWnd; return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
