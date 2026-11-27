using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TextOutline
{
    // A font TXSCAN can try: a TrueType family in one style, or an SHX font file.
    public sealed class FontCandidate
    {
        public string Name;
        public FontFamily Family;
        public FontStyle Style;
        public ShxFont Shx;
        public string ShxFile;      // file name for the AutoCAD text style, e.g. "romans.shx"

        public bool IsShx { get { return Shx != null; } }

        public override string ToString() { return Name; }

        public static FontCandidate TrueType(FontFamily family, FontStyle style)
        {
            FontCandidate c = new FontCandidate();
            c.Family = family;
            c.Style = style;
            c.Name = family.Name + (style == FontStyle.Regular ? "" : " " + style.ToString().Replace(", ", " "));
            return c;
        }

        public static FontCandidate FromShx(ShxFont font, string file)
        {
            FontCandidate c = new FontCandidate();
            c.Shx = font;
            c.ShxFile = file;
            c.Name = System.IO.Path.GetFileName(file) + " (SHX)";
            return c;
        }

        // Every installed TrueType family in each style it has. Symbol fonts are left out.
        public static List<FontCandidate> InstalledTrueType()
        {
            List<FontCandidate> list = new List<FontCandidate>();
            using (InstalledFontCollection ifc = new InstalledFontCollection())
            {
                foreach (FontFamily f in ifc.Families)
                {
                    string n = f.Name.ToLowerInvariant();
                    if (n.Contains("wingdings") || n.Contains("webdings") || n.Contains("symbol") || n.Contains("marlett")
                        || n.Contains("mdl2") || n.Contains("fluent icons") || n.Contains("holomdl") || n.Contains("emoji")
                        || n.Contains("extra") || n.StartsWith("bookshelf")) continue;
                    foreach (FontStyle s in new FontStyle[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                        if (f.IsStyleAvailable(s)) list.Add(TrueType(f, s));
                }
            }
            return list;
        }
    }

    public sealed class FontMatch
    {
        public FontCandidate Font;
        public double Score;            // 0..1, 1 = identical shapes
        public double ObliqueDeg;       // slant applied on top of the font
        public double WidthFactor;      // horizontal stretch needed to fit the scan
        public InkImage Render;         // the text in this font, fitted to the scan
        internal InkImage Source;       // the text in this font, cropped, before fitting
    }

    // Finds which fonts draw a given text most like the scanned letters.
    public static class FontMatcher
    {
        public const int WorkHeight = 72;       // scan and renders are compared at this height in pixels
        static readonly double[] Slants = { 0, 12 };

        public static List<FontMatch> Rank(InkImage scan, string text, IList<FontCandidate> fonts, int top)
        {
            List<FontMatch> all = new List<FontMatch>();
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return all;
            Rectangle sb = scan.InkBounds();
            if (sb.Width < 2 || sb.Height < 2) return all;

            InkImage s = scan.Crop(sb);
            double strokeRatio = s.StrokeWidth() / s.H;     // stroke thickness relative to the text block height
            string t = text.Trim();

            // Pass 1: every font, small and upright, to find the plausible ones quickly.
            Frame coarse = new Frame(s, CoarseHeight);
            List<FontMatch> quick = new List<FontMatch>();
            foreach (FontCandidate f in fonts)
            {
                FontMatch m = Try(f, t, 0, strokeRatio, CoarseEm, coarse);
                if (m != null) quick.Add(m);
            }
            quick.Sort(ByScore);

            // Pass 2: the shortlist at full size, upright and slanted.
            Frame fine = new Frame(s, WorkHeight);
            for (int i = 0; i < Math.Min(Shortlist, quick.Count); i++)
            {
                FontCandidate f = quick[i].Font;
                foreach (double slant in Slants)
                {
                    if (slant != 0 && !f.IsShx && (f.Style & FontStyle.Italic) != 0) continue;
                    FontMatch m = Try(f, t, slant, strokeRatio, FineEm, fine);
                    if (m != null) all.Add(m);
                }
            }
            all.Sort(ByScore);

            // The quick fit lines shapes up by their ink centre and spread, which can be a few percent
            // off. Fine-tune stretch and offset for the leaders, then rank again.
            for (int i = 0; i < Math.Min(Refined, all.Count); i++) Refine(all[i], fine.Target, fine.Grown, fine.Dist);
            all.Sort(ByScore);

            // One entry per font: keep its best slant.
            List<FontMatch> best = new List<FontMatch>();
            HashSet<string> seen = new HashSet<string>();
            foreach (FontMatch m in all)
            {
                if (seen.Contains(m.Font.Name)) continue;
                seen.Add(m.Font.Name);
                best.Add(m);
                if (best.Count >= top) break;
            }
            return best;
        }

        const int CoarseHeight = 44, Shortlist = 60, Refined = 12;
        const float CoarseEm = 64f, FineEm = 120f;

        static int ByScore(FontMatch a, FontMatch b) { return b.Score.CompareTo(a.Score); }

        // The scan prepared for comparing at one height.
        sealed class Frame
        {
            public readonly InkImage Target, Grown;
            public readonly int[] Dist;

            public Frame(InkImage scan, int h)
            {
                int w = Math.Max(4, (int)Math.Round(h * scan.W / (double)scan.H));
                Target = scan.Resize(w, h);
                Grown = Target.Dilate(1);
                Dist = Distance(Target);
            }
        }

        // Draws the text in one font and scores it against the scan, or returns null if it can't be drawn.
        static FontMatch Try(FontCandidate f, string text, double slant, double strokeRatio, float em, Frame fr)
        {
            InkImage r;
            try { r = Draw(f, text, slant, strokeRatio, em); }
            catch (Exception) { return null; }
            if (r == null) return null;
            Rectangle rb = r.InkBounds();
            if (rb.Width < 2 || rb.Height < 2) return null;
            InkImage rc = r.Crop(rb);

            // Line the render up with the scan by the centre and spread of their ink, which a few stray
            // scan pixels barely move (a bounding box would jump).
            double wf;
            InkImage fitted = FitByMoments(rc, fr.Target, out wf);
            if (fitted == null) return null;

            FontMatch m = new FontMatch();
            m.Font = f;
            m.Score = Score(fr.Target, fr.Grown, fr.Dist, fitted, wf, slant);
            m.ObliqueDeg = slant;
            m.WidthFactor = wf;
            m.Render = fitted;
            m.Source = rc;
            return m;
        }

        // Overlap tells bold from regular; the distance score tells shapes apart even when the scan made
        // strokes thicker or thinner than the font draws them. Real text is rarely stretched much, and
        // most of it is upright, so stretch and slant cost a little.
        static double Score(InkImage target, InkImage targetGrown, int[] targetDist, InkImage fitted, double wf, double slant)
        {
            double score = (0.4 * Dice(target, targetGrown, fitted, fitted.Dilate(1)))
                         + (0.6 * Chamfer(target, targetDist, fitted, Distance(fitted)));
            score *= 1 - (0.35 * Math.Min(1, Math.Abs(Math.Log(wf))));
            if (slant != 0) score *= 0.99;
            return score;
        }

        // Coordinate search on horizontal and vertical stretch and offset, starting from the moment fit.
        static void Refine(FontMatch m, InkImage target, InkImage targetGrown, int[] targetDist)
        {
            InkImage src = m.Source;
            double rcx, rcy, rsx, rsy, tcx, tcy, tsx, tsy;
            if (src == null || !Moments(src, out rcx, out rcy, out rsx, out rsy) || !Moments(target, out tcx, out tcy, out tsx, out tsy))
                return;
            double kx = tsx / rsx, ky = tsy / rsy;
            double[] p = { kx, ky, tcx, tcy };
            double[] step = { kx * 0.02, ky * 0.02, 1.0, 1.0 };
            double best = m.Score;
            InkImage bestImg = m.Render;
            for (int round = 0; round < 2; round++)
            {
                bool improved = true;
                for (int it = 0; it < 10 && improved; it++)
                {
                    improved = false;
                    for (int k = 0; k < 4; k++)
                        foreach (int sign in new int[] { 1, -1 })
                        {
                            double[] q = (double[])p.Clone();
                            q[k] += sign * step[k];
                            InkImage img = Place(src, target.W, target.H, rcx, rcy, q[2], q[3], q[0], q[1]);
                            double sc = Score(target, targetGrown, targetDist, img, q[0] / q[1], m.ObliqueDeg);
                            if (sc > best + 1e-6)
                            {
                                best = sc; bestImg = img; p = q; improved = true;
                            }
                        }
                }
                for (int k = 0; k < 4; k++) step[k] /= 2;
            }
            m.Score = best;
            m.Render = bestImg;
            m.WidthFactor = p[0] / p[1];
        }

        // Ranks the scan as it is and turned 180 degrees, and keeps the better way up (for when OCR could
        // not tell). Turns the scan in place when upside down was better.
        public static List<FontMatch> RankBothWays(ScanResult scan, string text, IList<FontCandidate> fonts, int top)
        {
            List<FontMatch> up = Rank(scan.Level, text, fonts, top);
            List<FontMatch> down = Rank(scan.Level.Flip180(), text, fonts, top);
            double su = up.Count > 0 ? up[0].Score : 0, sd = down.Count > 0 ? down[0].Score : 0;
            if (sd <= su) return up;
            ScanText.SetAngle(scan, scan.AngleDeg + 180);
            return down;
        }

        // Centre and spread (standard deviation) of the ink in x and y.
        public static bool Moments(InkImage img, out double cx, out double cy, out double sx, out double sy)
        {
            double n = 0, mx = 0, my = 0, xx = 0, yy = 0;
            for (int y = 0; y < img.H; y++)
                for (int x = 0; x < img.W; x++)
                    if (img.P[(y * img.W) + x])
                    {
                        double px = x + 0.5, py = y + 0.5;
                        n++; mx += px; my += py; xx += px * px; yy += py * py;
                    }
            cx = cy = sx = sy = 0;
            if (n < 4) return false;
            cx = mx / n; cy = my / n;
            sx = Math.Sqrt(Math.Max(1e-9, (xx / n) - (cx * cx)));
            sy = Math.Sqrt(Math.Max(1e-9, (yy / n) - (cy * cy)));
            return true;
        }

        // Draws the render into the target's frame so their ink centres and spreads coincide.
        // widthFactor = how much the render had to be stretched sideways (relative to its height scale).
        public static InkImage FitByMoments(InkImage render, InkImage target, out double widthFactor)
        {
            widthFactor = 1;
            double rcx, rcy, rsx, rsy, tcx, tcy, tsx, tsy;
            if (!Moments(render, out rcx, out rcy, out rsx, out rsy) || !Moments(target, out tcx, out tcy, out tsx, out tsy))
                return null;
            double kx = tsx / rsx, ky = tsy / rsy;
            widthFactor = kx / ky;
            return Place(render, target.W, target.H, rcx, rcy, tcx, tcy, kx, ky);
        }

        // Draws the render into a w x h frame: render point (rcx, rcy) lands on (tcx, tcy), scaled kx, ky.
        static InkImage Place(InkImage render, int w, int h, double rcx, double rcy, double tcx, double tcy, double kx, double ky)
        {
            InkImage o = new InkImage(w, h);
            for (int y = 0; y < o.H; y++)
            {
                int ry = (int)Math.Floor(((y + 0.5 - tcy) / ky) + rcy);
                for (int x = 0; x < o.W; x++)
                {
                    int rx = (int)Math.Floor(((x + 0.5 - tcx) / kx) + rcx);
                    o.P[(y * o.W) + x] = render[rx, ry];
                }
            }
            return o;
        }

        const int ChamferCap = 18;          // distances in thirds of a pixel: 18 = 6 px

        // 1 - mean distance (capped) from each ink pixel to the other image's ink, both ways (1 = same).
        public static double Chamfer(InkImage a, int[] aDist, InkImage b, int[] bDist)
        {
            long sa = 0, sb = 0;
            int na = 0, nb = 0;
            for (int i = 0; i < a.P.Length; i++)
            {
                if (a.P[i]) { na++; sa += Math.Min(ChamferCap, bDist[i]); }
                if (b.P[i]) { nb++; sb += Math.Min(ChamferCap, aDist[i]); }
            }
            if (na == 0 || nb == 0) return 0;
            return 1 - (0.5 * ((sa / (double)na) + (sb / (double)nb)) / ChamferCap);
        }

        // Distance from every pixel to the nearest ink pixel, in thirds of a pixel (3-4 chamfer).
        public static int[] Distance(InkImage img)
        {
            int w = img.W, h = img.H;
            int[] d = new int[w * h];
            const int Far = 1 << 20;
            for (int i = 0; i < d.Length; i++) d[i] = img.P[i] ? 0 : Far;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x, v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + 3);
                    if (y > 0)
                    {
                        v = Math.Min(v, d[i - w] + 3);
                        if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                        if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                    }
                    d[i] = v;
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = (y * w) + x, v = d[i];
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, d[i + w] + 3);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                    }
                    d[i] = v;
                }
            return d;
        }

        // Overlap of two ink images with one pixel of tolerance (1 = same shape).
        public static double Dice(InkImage a, InkImage aGrown, InkImage b, InkImage bGrown)
        {
            int na = 0, nb = 0, hitA = 0, hitB = 0;
            for (int i = 0; i < a.P.Length; i++)
            {
                if (a.P[i]) { na++; if (bGrown.P[i]) hitA++; }
                if (b.P[i]) { nb++; if (aGrown.P[i]) hitB++; }
            }
            return na + nb == 0 ? 0 : (hitA + hitB) / (double)(na + nb);
        }

        // Draws the text in a font as ink, em pixels per em, slanted by slantDeg.
        // SHX strokes get a pen as thick (relative to the text height) as the scanned strokes.
        public static InkImage Draw(FontCandidate f, string text, double slantDeg, double strokeRatio)
        {
            return Draw(f, text, slantDeg, strokeRatio, FineEm);
        }

        public static InkImage Draw(FontCandidate f, string text, double slantDeg, double strokeRatio, float em)
        {
            using (GraphicsPath path = new GraphicsPath(FillMode.Winding))
            {
                float pen = 0;
                if (f.IsShx)
                {
                    double adv;
                    int missing;
                    List<ShxStroke> strokes = f.Shx.Render(text, out adv, out missing);
                    if (strokes.Count == 0) return null;
                    double k = em / Math.Max(1, f.Shx.Above);
                    foreach (ShxStroke st in strokes)
                    {
                        ShxStroke flat = st.Flatten(8);
                        PointF[] pts = new PointF[flat.Count];
                        for (int i = 0; i < flat.Count; i++) pts[i] = new PointF((float)(flat.X[i] * k), (float)(-flat.Y[i] * k));
                        path.StartFigure();
                        path.AddLines(pts);
                    }
                    pen = (float)Math.Max(1.5, strokeRatio * em * 1.2);
                }
                else
                {
                    // Lay the text out very large, then scale down: GDI+ rounds every letter's advance to whole
                    // pixels at the layout size, and at 120 px that rounding visibly changes the spacing.
                    const float layout = 2000f;
                    using (StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        fmt.FormatFlags |= StringFormatFlags.NoWrap;
                        path.AddString(text, f.Family, (int)f.Style, layout, new PointF(0, 0), fmt);
                    }
                    if (path.PointCount == 0) return null;
                    using (Matrix down = new Matrix(em / layout, 0, 0, em / layout, 0, 0)) path.Transform(down);
                }

                if (slantDeg != 0)
                    using (Matrix shear = new Matrix(1, 0, -(float)Math.Tan(slantDeg * Math.PI / 180), 1, 0, 0))
                        path.Transform(shear);

                RectangleF b;
                using (Pen p = new Pen(Color.Black, Math.Max(1, pen)))
                    b = pen > 0 ? path.GetBounds(null, p) : path.GetBounds();
                int w = (int)Math.Ceiling(b.Width) + 4, h = (int)Math.Ceiling(b.Height) + 4;
                if (w < 2 || h < 2 || w > 20000 || h > 4000) return null;

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TranslateTransform(2 - b.X, 2 - b.Y);
                        if (pen > 0)
                            using (Pen p = new Pen(Color.Black, pen))
                            {
                                p.StartCap = LineCap.Round;
                                p.EndCap = LineCap.Round;
                                p.LineJoin = LineJoin.Round;
                                g.DrawPath(p, path);
                            }
                        else g.FillPath(Brushes.Black, path);
                    }
                    return Threshold(bmp);
                }
            }
        }

        static InkImage Threshold(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            byte[] raw = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, raw, 0, raw.Length);
            bmp.UnlockBits(d);
            InkImage img = new InkImage(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    img.P[(y * w) + x] = raw[(y * d.Stride) + (x * 3)] < 128;
            return img;
        }
    }
}
