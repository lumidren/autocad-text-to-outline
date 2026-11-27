using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TextOutline;

// Checks TXSCAN's image side without AutoCAD: fake point-cloud captures (text drawn in a known
// font, turned, broken into scattered dots with noise) must come back level, read correctly by
// Windows OCR (when this PC has it) and matched to the right font.
static class ScanTest
{
    static int passed, failed;

    static int Main(string[] args)
    {
        bool ocr = !(args.Length > 0 && args[0] == "--no-ocr");
        List<FontCandidate> fonts = Candidates();
        Console.WriteLine("{0} candidate font styles", fonts.Count);

        Case("painted marking: light dots on dark, Arial Bold, 23 deg", "PARKING 12", "Arial", FontStyle.Bold,
             23, true, 3, fonts, ocr, "Arial Bold");
        Case("engraved plate: dark dots on light, Times New Roman, -8 deg", "Station 4", "Times New Roman", FontStyle.Regular,
             -8, false, 3, fonts, ocr, "Times New Roman");
        Case("upside-down text: Verdana, 170 deg", "EXIT 3", "Verdana", FontStyle.Regular,
             170, true, 3, fonts, ocr, "Verdana");
        Case("solid capture (no dots): Courier New, 0 deg", "CODE 77", "Courier New", FontStyle.Regular,
             0, false, 0, fonts, ocr, "Courier New");

        // Corner mapping round trip.
        InkImage blank = new InkImage(300, 120);
        PointF q = blank.RotatedPoint(37, 40, 90);
        PointF back = blank.UnrotatedPoint(37, q.X, q.Y);
        Check("rotate / unrotate point round trip", Math.Abs(back.X - 40) < 1e-3 && Math.Abs(back.Y - 90) < 1e-3);

        Console.WriteLine();
        Console.WriteLine("{0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    static List<FontCandidate> Candidates()
    {
        string[] names = { "Arial", "Times New Roman", "Courier New", "Verdana", "Georgia", "Consolas",
                           "Segoe UI", "Tahoma", "Calibri", "Impact", "Trebuchet MS", "Comic Sans MS" };
        List<FontCandidate> list = new List<FontCandidate>();
        using (InstalledFontCollection ifc = new InstalledFontCollection())
            foreach (FontFamily f in ifc.Families)
                if (Array.IndexOf(names, f.Name) >= 0)
                    foreach (FontStyle s in new FontStyle[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic })
                        if (f.IsStyleAvailable(s)) list.Add(FontCandidate.TrueType(f, s));
        return list;
    }

    static void Case(string title, string text, string family, FontStyle style, double angle, bool light,
                     int dotSpacing, List<FontCandidate> fonts, bool ocr, string expectFont)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + title);
        Bitmap cap = FakeCapture(text, new FontFamily(family), style, angle, light, dotSpacing, 12345);
        Stopwatch sw = Stopwatch.StartNew();
        ScanResult r = ScanText.Analyze(cap, ocr, null);
        long tAnalyze = sw.ElapsedMilliseconds;
        string dump = Environment.GetEnvironmentVariable("TXSCAN_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            string tag = text.Replace(" ", "_");
            cap.Save(System.IO.Path.Combine(dump, tag + "-capture.png"), ImageFormat.Png);
            r.Clean.ToBitmap(0).Save(System.IO.Path.Combine(dump, tag + "-clean.png"), ImageFormat.Png);
            if (r.Level != null) r.Level.ToBitmap(8).Save(System.IO.Path.Combine(dump, tag + "-level.png"), ImageFormat.Png);
        }

        double want = angle;
        double err = Math.Abs(Norm(r.AngleDeg - want));
        if (!ocr) err = Math.Min(err, Math.Abs(Norm(r.AngleDeg - want + 180)));
        Check(string.Format("  text direction found: {0:F2} deg (true {1})", r.AngleDeg, angle), err <= 1.5);
        Check("  letters are " + (light ? "lighter" : "darker") + " than background", r.LightLetters == light);
        if (dotSpacing > 0) Check("  scan points classified (letter vs surface)", r.PointMode);

        string read = r.Text;
        if (ocr)
        {
            bool same = Squash(read) == Squash(text);
            Check(string.Format("  OCR read \"{0}\"{1}", read, r.OcrError.Length > 0 ? " (" + r.OcrError + ")" : ""), same);
        }
        sw.Restart();
        List<FontMatch> top = ocr ? FontMatcher.Rank(r.Level, text, fonts, 5) : FontMatcher.RankBothWays(r, text, fonts, 5);
        long tRank = sw.ElapsedMilliseconds;
        for (int i = 0; i < top.Count; i++)
            Console.WriteLine("     {0}. {1,-28} score {2:F3}  width {3:F2}  slant {4}", i + 1, top[i].Font.Name, top[i].Score,
                              top[i].WidthFactor, top[i].ObliqueDeg);
        Check("  best font is " + expectFont, top.Count > 0 && top[0].Font.Name == expectFont);
        Console.WriteLine("     analyse {0} ms, rank {1} ms", tAnalyze, tRank);
    }

    // Draws the text, turns it, then samples it like a scanner: dots on a jittered grid, a little
    // intensity noise and random stray points.
    static Bitmap FakeCapture(string text, FontFamily family, FontStyle style, double angle, bool light, int spacing, int seed)
    {
        const int W = 900, H = 500;
        Random rnd = new Random(seed);
        bool[] mask = new bool[W * H];
        using (Bitmap m = new Bitmap(W, H, PixelFormat.Format24bppRgb))
        {
            using (Graphics g = Graphics.FromImage(m))
            using (GraphicsPath p = new GraphicsPath())
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Real letters keep the font's exact spacing: lay out large, then scale to 90 px.
                p.AddString(text, family, (int)style, 2000, new PointF(0, 0), StringFormat.GenericTypographic);
                using (Matrix down = new Matrix(0.045f, 0, 0, 0.045f, 0, 0)) p.Transform(down);
                RectangleF b = p.GetBounds();
                g.TranslateTransform(W / 2f, H / 2f);
                g.RotateTransform(-(float)angle);                 // screen y is down: negative = anticlockwise
                g.TranslateTransform(-b.X - (b.Width / 2), -b.Y - (b.Height / 2));
                g.FillPath(Brushes.Black, p);
            }
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    mask[(y * W) + x] = m.GetPixel(x, y).R < 128;
        }

        Bitmap cap = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        Color bg = light ? Color.FromArgb(25, 25, 30) : Color.FromArgb(225, 222, 215);
        using (Graphics g = Graphics.FromImage(cap)) g.Clear(spacing > 0 ? (light ? Color.Black : Color.White) : bg);
        if (spacing == 0)
        {
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) cap.SetPixel(i % W, i / W, light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(40, 40, 40));
            return cap;
        }
        for (int gy = 0; gy < H; gy += spacing)
            for (int gx = 0; gx < W; gx += spacing)
            {
                int x = gx + rnd.Next(spacing), y = gy + rnd.Next(spacing);
                if (x >= W || y >= H) continue;
                bool letter = mask[(y * W) + x];
                int v = letter ? (light ? 200 : 50) : (light ? 60 : 190);
                v = Math.Max(0, Math.Min(255, v + rnd.Next(-40, 41)));
                cap.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
        for (int i = 0; i < 300; i++)
        {
            int v = light ? 220 : 30;
            cap.SetPixel(rnd.Next(W), rnd.Next(H), Color.FromArgb(v, v, v));
        }
        return cap;
    }

    static double Norm(double d)
    {
        while (d > 180) d -= 360;
        while (d <= -180) d += 360;
        return d;
    }

    static string Squash(string s) { return s.Replace(" ", "").ToUpperInvariant(); }

    static void Check(string name, bool ok)
    {
        Console.WriteLine("{0}  {1}", ok ? "PASS" : "FAIL", name);
        if (ok) passed++; else failed++;
    }
}
