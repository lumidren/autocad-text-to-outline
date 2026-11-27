using System;
using System.Collections.Generic;
using System.Drawing;

namespace TextOutline
{
    // What TXSCAN found in a captured window: the cleaned letters, their direction and the OCR text.
    public sealed class ScanResult
    {
        public InkImage Clean;          // letters only, in the captured image's orientation
        public bool LightLetters;       // letters were lighter than the background
        public int GapRadius;           // radius used to join point-cloud dots (0 = none)
        public bool PointMode;          // separate points on a plain view background were classified
        public double AngleDeg;         // text direction, anticlockwise on screen (includes 180 if upside down)
        public InkImage Level;          // letters turned upright and cropped to their ink
        public Rectangle LevelBox;      // where Level sits inside Clean.Rotate(AngleDeg)
        public string Text = "";
        public string OcrError = "";

        // The four corners of the letters' box, in captured-image pixels:
        // bottom-left, bottom-right, top-right, top-left of the upright text.
        public PointF[] Corners()
        {
            Rectangle b = LevelBox;
            return new PointF[]
            {
                Clean.UnrotatedPoint(AngleDeg, b.Left, b.Bottom),
                Clean.UnrotatedPoint(AngleDeg, b.Right, b.Bottom),
                Clean.UnrotatedPoint(AngleDeg, b.Right, b.Top),
                Clean.UnrotatedPoint(AngleDeg, b.Left, b.Top),
            };
        }
    }

    public static class ScanText
    {
        // Threshold, join the dots, drop specks, straighten, then read the text both ways up.
        public static ScanResult Analyze(Bitmap capture, bool runOcr, string ocrLanguage)
        {
            ScanResult r = new ScanResult();
            bool light, pointMode;
            InkImage ink = InkImage.FromCapture(capture, 12, out light, out pointMode);
            r.LightLetters = light;
            r.PointMode = pointMode;
            if (pointMode)
            {
                ink = ink.Close(1).Smooth(2);           // smooth the ragged edges the point fill leaves
            }
            else
            {
                r.GapRadius = ink.PointGapRadius();
                if (r.GapRadius > 0) ink = ink.Close(r.GapRadius);
            }
            ink = ink.RemoveBorderPieces().RemoveSpecks(0.03);
            r.Clean = ink;

            double angle = ink.SkewAngle();
            SetAngle(r, angle);
            if (r.Level == null) return r;

            if (runOcr)
            {
                List<string> up = WindowsOcr.Recognize(OcrImage(r.Level), ocrLanguage);
                string err = WindowsOcr.LastError;
                List<string> down = WindowsOcr.Recognize(OcrImage(r.Level.Flip180()), ocrLanguage);
                if (WindowsOcr.Quality(down) > WindowsOcr.Quality(up))
                {
                    SetAngle(r, angle + 180);
                    up = down;
                }
                r.Text = string.Join(" ", up.ToArray()).Trim();
                r.OcrError = r.Text.Length == 0 ? (err.Length > 0 ? err : WindowsOcr.LastError) : "";
            }
            return r;
        }

        public static void SetAngle(ScanResult r, double deg)
        {
            while (deg > 180) deg -= 360;
            while (deg <= -180) deg += 360;
            r.AngleDeg = deg;
            InkImage lev = r.Clean.Rotate(deg).KeepTextBand();
            Rectangle b = lev.InkBounds();
            r.LevelBox = b;
            r.Level = b.Width > 0 && b.Height > 0 ? lev.Crop(b) : null;
        }

        // OCR reads best with dark letters about 60 px tall and a white margin.
        static Bitmap OcrImage(InkImage level)
        {
            InkImage img = level;
            double k = 60.0 / Math.Max(1, level.H);
            if (k < 0.5 || k > 2)
                img = level.Resize(Math.Max(1, (int)Math.Round(level.W * k)), Math.Max(1, (int)Math.Round(level.H * k)));
            return img.ToBitmap(24);
        }
    }
}
