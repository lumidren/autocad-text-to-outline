using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TextOutline;

// Generates the illustrations in docs/ from the same outline code the plugin uses.
static class MakeDocImages
{
    static readonly Color Bg = Color.FromArgb(33, 40, 48);       // AutoCAD dark model space
    static readonly Color Ink = Color.FromArgb(235, 240, 245);
    static readonly Color Cyan = Color.FromArgb(80, 200, 255);
    static readonly Color Messy = Color.FromArgb(255, 170, 60);
    static readonly Color Label = Color.FromArgb(160, 172, 185);
    const int Scale = 2; // draw at 2x and downsample for smooth edges

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        BeforeAfter(dir + "\\before-after.png");
        ManualVsPlugin(dir + "\\manual-vs-plugin.png");
        Console.WriteLine("done");
    }

    static void BeforeAfter(string file)
    {
        FontFamily fam = new FontFamily("Arial");
        List<Contour> cs = GlyphOutline.GetContours("Text", fam, FontStyle.Bold);
        Draw(file, 1200, 330, delegate(Graphics g)
        {
            Caption(g, "BEFORE:  TEXT object (Arial Bold)", 40, 22);
            Caption(g, "AFTER  TX:  closed polylines", 660, 22);
            double s = Fit(cs); using (GraphicsPath p = ToPath(cs, s, 40, 230))
                g.FillPath(new SolidBrush(Ink), p);
            Arrow(g, 545, 165);
            CleanOutline(g, cs, s, 660, 230, Cyan);
            Caption(g, "4 outer outlines + 1 hole (e)", 660, 288);
        });
    }

    static void ManualVsPlugin(string file)
    {
        FontFamily fam = new FontFamily("Segoe Script");
        List<Contour> cs = GlyphOutline.GetContours("Hello", fam, FontStyle.Regular);
        Draw(file, 1200, 330, delegate(Graphics g)
        {
            Caption(g, "After EXPLODE:  overlapping + internal lines", 40, 22);
            Caption(g, "After TX:  only the outer edges", 660, 22);
            double s = Fit(cs); using (GraphicsPath p = ToPath(cs, s, 40, 220))
                g.DrawPath(new Pen(Messy, 1.6f), p);
            Arrow(g, 545, 165);
            CleanOutline(g, cs, s, 660, 220, Cyan);
            Caption(g, "overlaps merged, holes kept, every loop one closed polyline", 660, 288);
        });
    }

    // Draws only the boundary of the merged shape: stroke every contour, then paint the merged
    // interior over it, which erases internal lines and the inner half of the boundary strokes.
    static void CleanOutline(Graphics g, List<Contour> cs, double s, double ox, double oy, Color color)
    {
        Region merged = new Region();
        merged.MakeEmpty();
        bool first = true;
        foreach (Contour c in GlyphOutline.MergeOrder(cs))
            using (GraphicsPath p = ToPath(new List<Contour> { c }, s, ox, oy))
            {
                if (first) { if (!c.IsHole) { merged.Union(p); first = false; } continue; }
                if (c.IsHole) merged.Exclude(p); else merged.Union(p);
            }
        using (GraphicsPath all = ToPath(cs, s, ox, oy))
            g.DrawPath(new Pen(color, 3.2f), all);
        g.FillRegion(new SolidBrush(Bg), merged);
    }

    static void Arrow(Graphics g, float x, float y)
    {
        using (Pen pen = new Pen(Label, 4))
        {
            pen.CustomEndCap = new AdjustableArrowCap(4, 4);
            g.DrawLine(pen, x, y, x + 70, y);
        }
        g.DrawString("TX", new Font("Consolas", 13, FontStyle.Bold), new SolidBrush(Label), x + 12, y - 34);
    }

    static void Caption(Graphics g, string text, float x, float y)
    {
        g.DrawString(text, new Font("Segoe UI", 13), new SolidBrush(Label), x, y);
    }

    static void Draw(string file, int w, int h, Action<Graphics> body)
    {
        using (Bitmap big = new Bitmap(w * Scale, h * Scale))
        {
            using (Graphics g = Graphics.FromImage(big))
            {
                g.Clear(Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(Scale, Scale);
                body(g);
            }
            using (Bitmap small = new Bitmap(w, h))
            using (Graphics g2 = Graphics.FromImage(small))
            {
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.DrawImage(big, 0, 0, w, h);
                small.Save(file, ImageFormat.Png);
            }
        }
    }

    // Scale (font units -> pixels) that fits the text into a 480 x 170 px box.
    static double Fit(List<Contour> cs)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (Contour c in cs)
            for (int i = 0; i < c.Count; i++)
            {
                minX = Math.Min(minX, c.X[i]); maxX = Math.Max(maxX, c.X[i]);
                minY = Math.Min(minY, c.Y[i]); maxY = Math.Max(maxY, c.Y[i]);
            }
        return Math.Min(480 / (maxX - minX), 170 / (maxY - minY));
    }

    static GraphicsPath ToPath(List<Contour> contours, double s, double ox, double oy)
    {
        GraphicsPath p = new GraphicsPath(FillMode.Winding);
        foreach (Contour c in contours)
        {
            PointF[] pts = new PointF[c.Count];
            for (int i = 0; i < c.Count; i++) pts[i] = new PointF((float)(ox + c.X[i] * s), (float)(oy - c.Y[i] * s));
            p.AddPolygon(pts);
        }
        return p;
    }
}
