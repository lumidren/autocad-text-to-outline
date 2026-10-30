using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TextOutline;

// Checks the glyph contours and the solid/hole merge order without AutoCAD:
// the merged result (unite solids, cut holes, largest first) must cover exactly
// the same pixels as the font's own filled text.
static class OutlineTest
{
    static int Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : "outline-test.png";
        string text = "Hello o8B@ %&R ®Ø° Ag";
        string[][] cases = {
            new[] { "Arial", "Regular" }, new[] { "Times New Roman", "Bold, Italic" },
            new[] { "Segoe Script", "Regular" }, new[] { "Impact", "Regular" }, new[] { "Consolas", "Bold" } };

        const int rowH = 150, width = 1700;
        int failures = 0;
        using (Bitmap sheet = new Bitmap(width, rowH * cases.Length, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int k = 0; k < cases.Length; k++)
            {
                FontFamily fam = GlyphOutline.FamilyFromName(cases[k][0]);
                if (fam == null) { Console.WriteLine("missing font " + cases[k][0]); continue; }
                FontStyle style = (FontStyle)Enum.Parse(typeof(FontStyle), cases[k][1]);
                List<Contour> contours = GlyphOutline.GetContours(text, fam, style);

                const double s = 0.1; // font units -> pixels
                double ox = 10, oy = (k * rowH) + 110;

                // Reference: the font's own filled outline (nonzero winding)
                GraphicsPath reference = ToPath(contours, s, 0, 0);
                // Plugin result: merged contours
                Region merged = new Region();
                merged.MakeEmpty();
                bool first = true;
                foreach (Contour c in GlyphOutline.MergeOrder(contours))
                {
                    using (GraphicsPath p = ToPath(new List<Contour> { c }, s, 0, 0))
                    {
                        if (first) { if (!c.IsHole) { merged.Union(p); first = false; } continue; }
                        if (c.IsHole) merged.Exclude(p); else merged.Union(p);
                    }
                }

                int diff = CountDiff(reference, merged);
                int holes = contours.FindAll(c => c.IsHole).Count;
                string verdict = diff <= 40 ? "OK" : "MISMATCH";
                if (diff > 40) failures++;
                Console.WriteLine("{0,-18} {1,-14} contours={2,3} holes={3,2} pixelDiff={4,4} {5}",
                    cases[k][0], cases[k][1], contours.Count, holes, diff, verdict);

                // Draw: merged fill in light blue, outer edges dark blue, hole edges red
                using (Matrix m = new Matrix(1, 0, 0, 1, (float)ox, (float)oy))
                {
                    Region shown = merged.Clone();
                    shown.Transform(m);
                    g.FillRegion(new SolidBrush(Color.FromArgb(70, 30, 110, 220)), shown);
                }
                foreach (Contour c in contours)
                    using (GraphicsPath p = ToPath(new List<Contour> { c }, s, ox, oy))
                        g.DrawPath(new Pen(c.IsHole ? Color.Crimson : Color.Navy, 1.4f), p);
                g.DrawString(cases[k][0] + " " + cases[k][1] + "   " + verdict + " (diff " + diff + " px)",
                    new Font("Segoe UI", 10), Brushes.Black, 10, k * rowH + 4);
            }
            sheet.Save(outFile, ImageFormat.Png);
        }
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures;
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

    // Area (in pixels) where the two shapes disagree, both rasterized the same way as regions.
    static int CountDiff(GraphicsPath reference, Region merged)
    {
        using (Region x = new Region(reference))
        using (Matrix id = new Matrix())
        {
            x.Xor(merged);
            double area = 0;
            foreach (RectangleF r in x.GetRegionScans(id)) area += r.Width * r.Height;
            return (int)Math.Round(area);
        }
    }
}
