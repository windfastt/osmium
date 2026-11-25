// Layout: logical units only
using System.Drawing.Drawing2D;

namespace Osmium.App;

/// <summary>
/// Pens and brushes for custom-drawn controls, made once per colour and width and kept for the process (the palette
/// has a few dozen colours), so painting creates no GDI objects. Never dispose what these return.
/// </summary>
internal static class Paints
{
    private static readonly Dictionary<int, SolidBrush> Brushes = [];
    private static readonly Dictionary<(int, float), Pen> Pens = [];

    public static SolidBrush Brush(Color c)
    {
        int key = c.ToArgb();
        if (!Brushes.TryGetValue(key, out var b)) Brushes[key] = b = new SolidBrush(c);
        return b;
    }

    public static Pen Pen(Color c, float width = 1f)
    {
        var key = (c.ToArgb(), width);
        if (!Pens.TryGetValue(key, out var p)) Pens[key] = p = new Pen(c, width);
        return p;
    }
}

/// <summary>Drawing helpers: rounded shapes that sit on whole pixels, so one-pixel lines stay crisp.</summary>
internal static class Draw
{
    /// <summary>
    /// A rounded rectangle's outline. With <paramref name="stroke"/> the path runs through the middle of the pixels at
    /// the rectangle's edge (x + 0.5), so a one-pixel pen covers exactly the outermost pixels; without, it encloses the
    /// rectangle for filling.
    /// </summary>
    public static GraphicsPath Rounded(RectangleF r, float radius, bool stroke = false)
    {
        if (stroke) r = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills a rounded rectangle and draws its one-pixel border (null: none), anti-aliased.</summary>
    public static void RoundedBox(Graphics g, Rectangle r, float radius, Color? fill, Color? border, float borderWidth = 1f)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var smoothing = g.SmoothingMode;
        var offset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half; // pixel n spans n..n+1: a path at n + 0.5 runs through its middle
        if (fill is { } f)
        {
            using var path = Rounded(r, radius);
            g.FillPath(Paints.Brush(f), path);
        }
        if (border is { } b)
        {
            float inset = borderWidth / 2f;
            var rect = new RectangleF(r.X + inset, r.Y + inset, r.Width - borderWidth, r.Height - borderWidth);
            using var path = Rounded(rect, Math.Max(0, radius - inset));
            g.DrawPath(Paints.Pen(b, borderWidth), path);
        }
        g.SmoothingMode = smoothing;
        g.PixelOffsetMode = offset;
    }

    /// <summary>
    /// Paints the corners of <paramref name="r"/> outside its rounded outline in <paramref name="outside"/>: for native
    /// child windows (lists) that cannot be clipped to a rounded card.
    /// </summary>
    public static void Corners(Graphics g, Rectangle r, float radius, Color outside)
    {
        var smoothing = g.SmoothingMode;
        var offset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var path = Rounded(r, radius);
        path.AddRectangle(r); // the even-odd fill covers what lies outside the rounded outline
        path.FillMode = FillMode.Alternate;
        g.FillPath(Paints.Brush(outside), path);
        g.SmoothingMode = smoothing;
        g.PixelOffsetMode = offset;
    }

    /// <summary>The background colour behind <paramref name="c"/>: its parent's, or the page's.</summary>
    public static Color Behind(Control c) => c.Parent?.BackColor is { A: 255 } bg ? bg : Theme.P.Page;
}
