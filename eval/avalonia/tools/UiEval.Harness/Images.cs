using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace UiEval;

/// <summary>Captures and pixel statistics (System.Drawing; Windows only).</summary>
internal sealed class Img
{
    public int W { get; }
    public int H { get; }
    public int[] Px { get; } // 0xRRGGBB

    public Img(int w, int h, int[] px) { W = w; H = h; Px = px; }

    public static Img Load(string path)
    {
        using var b = new Bitmap(path);
        return From(b);
    }

    public static Img From(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * b.Width, b.Width);
            for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
            return new Img(b.Width, b.Height, px);
        }
        finally { b.UnlockBits(d); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[W];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++) row[x] = Px[y * W + x] | unchecked((int)0xFF000000);
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, W);
            }
        }
        finally { b.UnlockBits(d); }
        b.Save(path, ImageFormat.Png);
    }

    /// <summary>The screen (or a window through PrintWindow) inside a rectangle, in physical pixels.</summary>
    public static Img? Capture(IntPtr hWnd, Win.RECT r, bool printWindow)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;
        using var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            if (printWindow)
            {
                var hdc = g.GetHdc();
                bool ok;
                try { ok = PrintWindow(hWnd, hdc, 1 | 2); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok) return null;
            }
            else
            {
                // BitBlt with CAPTUREBLT (layered windows included); Graphics.CopyFromScreen rejects that flag combination.
                var hdc = g.GetHdc();
                var screen = GetDC(IntPtr.Zero);
                bool ok;
                try { ok = BitBlt(hdc, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); }
                finally { ReleaseDC(IntPtr.Zero, screen); g.ReleaseHdc(hdc); }
                if (!ok) return null;
            }
        }
        return From(b);
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    public Img Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, W); y = Math.Clamp(y, 0, H);
        w = Math.Clamp(w, 0, W - x); h = Math.Clamp(h, 0, H - y);
        var px = new int[w * h];
        for (int r = 0; r < h; r++) Array.Copy(Px, (y + r) * W + x, px, r * w, w);
        return new Img(w, h, px);
    }

    /// <summary>The most common colours and their share, like compare.py's dominant colours.</summary>
    public List<(int Rgb, double Share)> Top(int n = 3)
    {
        var c = new Dictionary<int, int>();
        foreach (var p in Px) c[p] = c.GetValueOrDefault(p) + 1;
        return [.. c.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, (double)kv.Value / Math.Max(1, Px.Length)))];
    }

    /// <summary>Share of pixels whose largest channel difference is within a tolerance (same size only).</summary>
    public double SameShare(Img o, int tol)
    {
        if (o.W != W || o.H != H) return 0;
        int same = 0;
        for (int i = 0; i < Px.Length; i++) if (Near(Px[i], o.Px[i], tol)) same++;
        return (double)same / Math.Max(1, Px.Length);
    }

    public double MeanAbsDiff(Img o)
    {
        int w = Math.Min(W, o.W), h = Math.Min(H, o.H);
        double sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int a = Px[y * W + x], b = o.Px[y * o.W + x];
                sum += Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255));
            }
        return sum / Math.Max(1, w * h * 3);
    }

    public bool IsBlank()
    {
        var t = Top(1);
        return t.Count == 0 || t[0].Share > 0.995;
    }

    public static bool Near(int a, int b, int tol) =>
        Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tol && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tol && Math.Abs((a & 255) - (b & 255)) <= tol;

    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6");
}
