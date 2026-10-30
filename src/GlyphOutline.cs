using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace TextOutline
{
    // A closed contour of a glyph, in font units (y up, baseline at y = 0).
    public class Contour
    {
        public List<double> X = new List<double>();
        public List<double> Y = new List<double>();
        public bool IsHole;

        public int Count { get { return X.Count; } }

        public double SignedArea()
        {
            double a = 0;
            int n = X.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
                a += (X[j] * Y[i]) - (X[i] * Y[j]);
            return a / 2.0;
        }
    }

    // Reads glyph outlines straight from a TrueType/OpenType font (no AutoCAD dependency).
    public static class GlyphOutline
    {
        // Glyphs are generated at this em size; the result is scaled to the AutoCAD text afterwards.
        public const float EmSize = 1000f;

        // Max deviation (in font units) between a glyph curve and its line segments: 0.02 % of the em.
        public static float Flatness = 0.2f;

        static readonly Dictionary<string, PrivateFontCollection> fontFiles =
            new Dictionary<string, PrivateFontCollection>(StringComparer.OrdinalIgnoreCase);

        public static FontFamily FamilyFromName(string typeFace)
        {
            if (string.IsNullOrEmpty(typeFace)) return null;
            try { return new FontFamily(typeFace); }
            catch (ArgumentException) { return null; }
        }

        public static FontFamily FamilyFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            PrivateFontCollection pfc;
            if (!fontFiles.TryGetValue(path, out pfc))
            {
                pfc = new PrivateFontCollection();
                pfc.AddFontFile(path);
                fontFiles[path] = pfc;
            }
            return pfc.Families.Length > 0 ? pfc.Families[0] : null;
        }

        public static FontStyle AvailableStyle(FontFamily family, FontStyle wanted)
        {
            FontStyle[] tries = { wanted, wanted & ~FontStyle.Italic, wanted & ~FontStyle.Bold,
                                  FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic };
            foreach (FontStyle s in tries)
                if (family.IsStyleAvailable(s)) return s;
            return FontStyle.Regular;
        }

        // Returns every closed contour of the string, marked as solid or hole.
        public static List<Contour> GetContours(string text, FontFamily family, FontStyle style)
        {
            List<Contour> result = new List<Contour>();
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return result;

            style = AvailableStyle(family, style);
            double ascent = EmSize * family.GetCellAscent(style) / (double)family.GetEmHeight(style);

            using (GraphicsPath path = new GraphicsPath(FillMode.Winding))
            using (StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (Matrix identity = new Matrix())
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                path.AddString(text, family, (int)style, EmSize, new PointF(0, 0), fmt);
                if (path.PointCount == 0) return result;
                path.Flatten(identity, Flatness);

                PointF[] pts = path.PathPoints;
                byte[] types = path.PathTypes;
                Contour cur = null;
                for (int i = 0; i < pts.Length; i++)
                {
                    byte t = types[i];
                    if ((t & 0x07) == 0)
                    {
                        Finish(cur, result);
                        cur = new Contour();
                    }
                    if (cur == null) cur = new Contour();
                    double x = pts[i].X;
                    double y = ascent - pts[i].Y;
                    int n = cur.Count;
                    if (n == 0 || Math.Abs(cur.X[n - 1] - x) > 1e-4 || Math.Abs(cur.Y[n - 1] - y) > 1e-4)
                    {
                        cur.X.Add(x);
                        cur.Y.Add(y);
                    }
                    if ((t & 0x80) != 0)
                    {
                        Finish(cur, result);
                        cur = null;
                    }
                }
                Finish(cur, result);
            }

            Classify(result);
            return result;
        }

        static void Finish(Contour c, List<Contour> into)
        {
            if (c == null) return;
            int n = c.Count;
            if (n > 1 && Math.Abs(c.X[0] - c.X[n - 1]) < 1e-4 && Math.Abs(c.Y[0] - c.Y[n - 1]) < 1e-4)
            {
                c.X.RemoveAt(n - 1);
                c.Y.RemoveAt(n - 1);
            }
            if (c.Count >= 3 && Math.Abs(c.SignedArea()) > 1e-3) into.Add(c);
        }

        // Within a font all outer contours share one winding direction and holes use the other.
        // The biggest contour is always an outer one, so its direction tells us which is which.
        static void Classify(List<Contour> contours)
        {
            double biggest = 0;
            foreach (Contour c in contours)
            {
                double a = c.SignedArea();
                if (Math.Abs(a) > Math.Abs(biggest)) biggest = a;
            }
            foreach (Contour c in contours)
                c.IsHole = Math.Sign(c.SignedArea()) != Math.Sign(biggest);
        }

        // Order in which contours are merged: largest first, so islands inside holes
        // (e.g. the "R" inside "®") are added back after the hole is cut.
        public static List<Contour> MergeOrder(List<Contour> contours)
        {
            List<Contour> sorted = new List<Contour>(contours);
            sorted.Sort(delegate(Contour a, Contour b)
            {
                return Math.Abs(b.SignedArea()).CompareTo(Math.Abs(a.SignedArea()));
            });
            return sorted;
        }
    }
}
