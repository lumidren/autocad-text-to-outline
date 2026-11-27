using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TextOutline
{
    // A black-and-white image of letters ("ink") taken from a point cloud view, with the clean-up
    // steps TXSCAN needs: threshold, gap filling, speck removal, straightening and cropping.
    // No AutoCAD dependency. Pixel (0,0) is the top-left corner, y grows downwards.
    public sealed class InkImage
    {
        public readonly int W, H;
        public readonly bool[] P;

        public InkImage(int w, int h)
        {
            W = Math.Max(1, w);
            H = Math.Max(1, h);
            P = new bool[W * H];
        }

        public bool this[int x, int y]
        {
            get { return x >= 0 && y >= 0 && x < W && y < H && P[(y * W) + x]; }
            set { if (x >= 0 && y >= 0 && x < W && y < H) P[(y * W) + x] = value; }
        }

        public int Count
        {
            get { int n = 0; foreach (bool b in P) if (b) n++; return n; }
        }

        // ------------------------------------------------------------------ from a picture

        // Thresholds a picture with Otsu's method. The letters are whichever side covers less of the
        // window, so light-on-dark (painted marks in an intensity view) and dark-on-light both work.
        public static InkImage FromBitmap(Bitmap bmp, out bool lightLetters)
        {
            byte[] g = Gray(bmp);
            int w = bmp.Width, h = bmp.Height;
            int[] hist = new int[256];
            foreach (byte v in g) hist[v]++;
            int t;
            bool high;
            if (LetterSplit(hist, g.Length, out t, out high))
            {
                lightLetters = high;
            }
            else
            {
                t = Otsu(hist, g.Length);
                int above = 0;
                for (int i = t + 1; i < 256; i++) above += hist[i];
                lightLetters = above < g.Length - above;
            }

            InkImage img = new InkImage(w, h);
            for (int i = 0; i < g.Length; i++) img.P[i] = lightLetters ? g[i] > t : g[i] <= t;
            return img;
        }

        // A point-cloud view has three kinds of pixels: the empty view background between points,
        // surface points and letter points. When one exact colour covers a good part of the window it is
        // that empty background: only the real points are thresholded, and every background pixel then
        // takes the class of its nearest point (up to maxGap pixels away). Otherwise this is FromBitmap.
        public static InkImage FromCapture(Bitmap bmp, int maxGap, out bool lightLetters, out bool pointMode)
        {
            pointMode = false;
            int w = bmp.Width, h = bmp.Height;
            int[] rgb;
            byte[] g = Gray(bmp, out rgb);

            Dictionary<int, int> freq = new Dictionary<int, int>();
            foreach (int c in rgb) { int n; freq.TryGetValue(c, out n); freq[c] = n + 1; }
            int mode = 0, modeN = 0;
            foreach (KeyValuePair<int, int> kv in freq) if (kv.Value > modeN) { modeN = kv.Value; mode = kv.Key; }
            if (modeN < 0.15 * rgb.Length || modeN > 0.995 * rgb.Length) return FromBitmap(bmp, out lightLetters);

            int[] hist = new int[256];
            int points = 0;
            double canvasGray = 0;
            for (int i = 0; i < rgb.Length; i++)
            {
                if (rgb[i] == mode) { canvasGray = g[i]; continue; }
                hist[g[i]]++;
                points++;
            }
            int t;
            bool letterHigh;
            InkImage img = new InkImage(w, h);
            if (!LetterSplit(hist, points, out t, out letterHigh))
            {
                // The points are all one kind: they are the letters, the plain colour is the background.
                double mean = 0;
                for (int i = 0; i < 256; i++) mean += i * (double)hist[i];
                lightLetters = mean / Math.Max(1, points) > canvasGray;
                for (int i = 0; i < rgb.Length; i++) img.P[i] = rgb[i] != mode;
                return img;
            }

            pointMode = true;
            lightLetters = letterHigh;
            sbyte[] cls = new sbyte[rgb.Length];             // 1 letter, 0 surface, -1 not known yet
            Queue<int> q = new Queue<int>();
            int[] dist = new int[rgb.Length];
            for (int i = 0; i < rgb.Length; i++)
            {
                if (rgb[i] == mode) { cls[i] = -1; continue; }
                bool hi = g[i] > t;
                cls[i] = (sbyte)((hi == lightLetters) ? 1 : 0);
                q.Enqueue(i);
            }
            // Nearest-point fill (breadth first, so each gap pixel takes the class of a closest point).
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                if (dist[c] >= maxGap) continue;
                int cx = c % w, cy = c / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = cx + dx, yy = cy + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int j = (yy * w) + xx;
                        if (cls[j] != -1) continue;
                        cls[j] = cls[c];
                        dist[j] = dist[c] + 1;
                        q.Enqueue(j);
                    }
            }
            for (int i = 0; i < cls.Length; i++) img.P[i] = cls[i] == 1;
            return img;
        }

        static byte[] Gray(Bitmap src)
        {
            int[] rgb;
            return Gray(src, out rgb);
        }

        static byte[] Gray(Bitmap src, out int[] rgb)
        {
            int w = src.Width, h = src.Height;
            rgb = new int[w * h];
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics gr = Graphics.FromImage(bmp)) gr.DrawImage(src, 0, 0, w, h);
                BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] raw = new byte[d.Stride * h];
                Marshal.Copy(d.Scan0, raw, 0, raw.Length);
                bmp.UnlockBits(d);
                byte[] g = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * d.Stride) + (x * 4);
                        g[(y * w) + x] = (byte)(((raw[o + 2] * 299) + (raw[o + 1] * 587) + (raw[o] * 114)) / 1000);
                        rgb[(y * w) + x] = (raw[o + 2] << 16) | (raw[o + 1] << 8) | raw[o];
                    }
                return g;
            }
        }

        static int Otsu(int[] hist, int total)
        {
            double eta;
            return Otsu(hist, total, out eta);
        }

        // Letters are usually a few percent of the points, which makes Otsu split the big surface group
        // instead. So: the surface is the median value with its spread (MAD); the letters are the clear
        // outliers on one side; the threshold goes midway between the two groups' medians.
        // Returns false when there are no clear outliers (all points look alike).
        public static bool LetterSplit(int[] hist, int total, out int threshold, out bool letterHigh)
        {
            threshold = 127;
            letterHigh = false;
            if (total < 20) return false;
            int med = Percentile(hist, total, 0.5);
            int[] dev = new int[256];
            for (int i = 0; i < 256; i++) dev[Math.Abs(i - med)] += hist[i];
            double s = Math.Max(4, 1.4826 * Percentile(dev, total, 0.5));
            int lo = (int)Math.Floor(med - (2.5 * s)), hi = (int)Math.Ceiling(med + (2.5 * s));

            int[] low = new int[256], high = new int[256];
            int nLow = 0, nHigh = 0;
            for (int i = 0; i < 256; i++)
            {
                if (i < lo) { low[i] = hist[i]; nLow += hist[i]; }
                if (i > hi) { high[i] = hist[i]; nHigh += hist[i]; }
            }
            letterHigh = nHigh > nLow;
            int n = Math.Max(nLow, nHigh);
            if (n < Math.Max(10, 0.005 * total)) return false;
            int letterMed = letterHigh ? Percentile(high, nHigh, 0.5) : Percentile(low, nLow, 0.5);
            threshold = (med + letterMed) / 2;
            return true;
        }

        static int Percentile(int[] hist, int total, double q)
        {
            long want = (long)Math.Ceiling(total * q), acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                acc += hist[i];
                if (acc >= want && acc > 0) return i;
            }
            return hist.Length - 1;
        }

        // Otsu threshold; eta (0..1) says how cleanly the two groups separate.
        static int Otsu(int[] hist, int total, out double eta)
        {
            double sum = 0, sumSq = 0;
            for (int i = 0; i < 256; i++) { sum += i * (double)hist[i]; sumSq += i * (double)i * hist[i]; }
            double mean = total > 0 ? sum / total : 0;
            double varTotal = total > 0 ? (sumSq / total) - (mean * mean) : 0;
            double sumB = 0, best = -1;
            int wB = 0, t = 127;
            for (int i = 0; i < 256; i++)
            {
                wB += hist[i];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;
                sumB += i * (double)hist[i];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; t = i; }
            }
            eta = varTotal > 1e-9 && total > 0 ? Math.Max(0, best) / ((double)total * total) / varTotal : 0;
            return t;
        }

        // ------------------------------------------------------------------ clean-up

        // Point clouds show letters as separate dots. When most ink pieces are tiny dots, close the gaps
        // (grow then shrink by about half the dot spacing) so each letter becomes one solid shape.
        public InkImage FillPointGaps()
        {
            int r = PointGapRadius();
            return r > 0 ? Close(r) : this;
        }

        public int PointGapRadius()
        {
            int[] label;
            List<int> areas = Components(out label);
            if (areas.Count < 20) return 0;
            int ink = Count;
            if (ink / (double)areas.Count > 12) return 0;      // mostly solid shapes already

            // Median distance from a dot to the nearest pixel of another dot.
            List<int> d = new List<int>();
            int step = Math.Max(1, ink / 1500), k = 0;
            for (int i = 0; i < P.Length; i++)
            {
                if (!P[i] || (k++ % step) != 0) continue;
                int x = i % W, y = i / W, me = label[i];
                for (int r = 1; r <= 12; r++)
                {
                    bool found = false;
                    for (int dy = -r; dy <= r && !found; dy++)
                        for (int dx = -r; dx <= r && !found; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                            int j = (yy * W) + xx;
                            if (P[j] && label[j] != me) found = true;
                        }
                    if (found) { d.Add(r); break; }
                }
            }
            if (d.Count == 0) return 0;
            d.Sort();
            return Math.Min(6, (d[d.Count / 2] + 1) / 2 + 1);
        }

        // Morphological closing with a disc of radius r.
        public InkImage Close(int r)
        {
            return Grow(r, true).Grow(r, false);
        }

        InkImage Grow(int r, bool dilate)
        {
            InkImage o = new InkImage(W, H);
            List<int[]> disc = new List<int[]>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if ((dx * dx) + (dy * dy) <= (r * r) + r) disc.Add(new int[] { dx, dy });
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    bool v = !dilate;
                    foreach (int[] o2 in disc)
                    {
                        int xx = x + o2[0], yy = y + o2[1];
                        bool p = xx >= 0 && yy >= 0 && xx < W && yy < H ? P[(yy * W) + xx] : !dilate;
                        if (dilate && p) { v = true; break; }
                        if (!dilate && !p) { v = false; break; }
                    }
                    o.P[(y * W) + x] = v;
                }
            return o;
        }

        // 8-connected ink pieces. Returns each piece's area; label[i] is the piece of pixel i (-1 = none).
        public List<int> Components(out int[] label)
        {
            label = new int[P.Length];
            for (int i = 0; i < label.Length; i++) label[i] = -1;
            List<int> areas = new List<int>();
            Stack<int> st = new Stack<int>();
            for (int i = 0; i < P.Length; i++)
            {
                if (!P[i] || label[i] >= 0) continue;
                int id = areas.Count, area = 0;
                label[i] = id;
                st.Push(i);
                while (st.Count > 0)
                {
                    int c = st.Pop();
                    area++;
                    int cx = c % W, cy = c / W;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = cx + dx, yy = cy + dy;
                            if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                            int j = (yy * W) + xx;
                            if (P[j] && label[j] < 0) { label[j] = id; st.Push(j); }
                        }
                }
                areas.Add(area);
            }
            return areas;
        }

        // Drops ink pieces smaller than a fraction of the biggest one (stray scan points, dirt).
        public InkImage RemoveSpecks(double fractionOfLargest)
        {
            int[] label;
            List<int> areas = Components(out label);
            int max = 0;
            foreach (int a in areas) max = Math.Max(max, a);
            int min = Math.Max(2, (int)(max * fractionOfLargest));
            InkImage o = new InkImage(W, H);
            for (int i = 0; i < P.Length; i++) o.P[i] = P[i] && areas[label[i]] >= min;
            return o;
        }

        // ------------------------------------------------------------------ geometry

        // Angle (degrees, -90..90) by which the text line is turned anticlockwise from horizontal:
        // the angle whose projection gives the sharpest row profile.
        public double SkewAngle()
        {
            List<int> xs = new List<int>(), ys = new List<int>();
            int step = Math.Max(1, Count / 20000), k = 0;
            for (int i = 0; i < P.Length; i++)
                if (P[i] && (k++ % step) == 0) { xs.Add(i % W); ys.Add(i / W); }
            if (xs.Count < 2) return 0;

            double best = 0, bestScore = double.MinValue;
            for (double a = -90; a < 90; a += 2) Score(xs, ys, a, ref best, ref bestScore);
            double c = best;
            for (double a = c - 2; a <= c + 2; a += 0.25) Score(xs, ys, a, ref best, ref bestScore);
            if (best >= 90) best -= 180;
            if (best < -90) best += 180;
            return best;
        }

        static void Score(List<int> xs, List<int> ys, double deg, ref double best, ref double bestScore)
        {
            double a = deg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
            Dictionary<int, int> bins = new Dictionary<int, int>();
            for (int i = 0; i < xs.Count; i++)
            {
                // Row of the point once the image is turned back by deg (y down, anticlockwise on screen).
                int row = (int)Math.Floor((ys[i] * c) + (xs[i] * s));
                int n;
                bins.TryGetValue(row, out n);
                bins[row] = n + 1;
            }
            double sc = 0;
            foreach (int n in bins.Values) sc += (double)n * n;
            if (sc > bestScore) { bestScore = sc; best = deg; }
        }

        // Turns the image clockwise on screen by deg (so text tilted anticlockwise by deg becomes level).
        // The result is big enough to hold the whole turned image.
        public InkImage Rotate(double deg)
        {
            double a = deg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
            int nw = (int)Math.Ceiling((Math.Abs(W * c)) + Math.Abs(H * s)) + 2;
            int nh = (int)Math.Ceiling((Math.Abs(W * s)) + Math.Abs(H * c)) + 2;
            InkImage o = new InkImage(nw, nh);
            double cx = W / 2.0, cy = H / 2.0, ncx = nw / 2.0, ncy = nh / 2.0;
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    // Inverse map: output pixel back into the source image.
                    double dx = x + 0.5 - ncx, dy = y + 0.5 - ncy;
                    double sx = (dx * c) + (dy * s) + cx, sy = (-dx * s) + (dy * c) + cy;
                    o.P[(y * nw) + x] = this[(int)Math.Floor(sx), (int)Math.Floor(sy)];
                }
            return o;
        }

        // Maps a point of this image to the image Rotate(deg) returns.
        public PointF RotatedPoint(double deg, double x, double y)
        {
            double a = deg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
            int nw = (int)Math.Ceiling((Math.Abs(W * c)) + Math.Abs(H * s)) + 2;
            int nh = (int)Math.Ceiling((Math.Abs(W * s)) + Math.Abs(H * c)) + 2;
            double dx = x - (W / 2.0), dy = y - (H / 2.0);
            // Forward map, screen-clockwise by deg in y-down pixels: (c, -s; s, c).
            return new PointF((float)((dx * c) - (dy * s) + (nw / 2.0)), (float)((dx * s) + (dy * c) + (nh / 2.0)));
        }

        // Maps a point of the image Rotate(deg) returns back to this image (inverse of RotatedPoint).
        public PointF UnrotatedPoint(double deg, double x, double y)
        {
            double a = deg * Math.PI / 180, s = Math.Sin(a), c = Math.Cos(a);
            int nw = (int)Math.Ceiling((Math.Abs(W * c)) + Math.Abs(H * s)) + 2;
            int nh = (int)Math.Ceiling((Math.Abs(W * s)) + Math.Abs(H * c)) + 2;
            double dx = x - (nw / 2.0), dy = y - (nh / 2.0);
            return new PointF((float)((dx * c) + (dy * s) + (W / 2.0)), (float)((-dx * s) + (dy * c) + (H / 2.0)));
        }

        // Drops ink pieces that touch the image border (cursor lines, cut-off neighbours, UCS icon).
        public InkImage RemoveBorderPieces()
        {
            int[] label;
            List<int> areas = Components(out label);
            bool[] touch = new bool[areas.Count];
            for (int x = 0; x < W; x++)
            {
                if (label[x] >= 0) touch[label[x]] = true;
                int j = ((H - 1) * W) + x;
                if (label[j] >= 0) touch[label[j]] = true;
            }
            for (int y = 0; y < H; y++)
            {
                int a = y * W, b = (y * W) + W - 1;
                if (label[a] >= 0) touch[label[a]] = true;
                if (label[b] >= 0) touch[label[b]] = true;
            }
            InkImage o = new InkImage(W, H);
            for (int i = 0; i < P.Length; i++) o.P[i] = P[i] && !touch[label[i]];
            return o;
        }

        // On level text: keeps only ink pieces that reach into the band of rows and columns holding
        // almost all the ink, so a stray scan point beside the text cannot stretch the text box.
        public InkImage KeepTextBand()
        {
            int total = Count;
            if (total == 0) return this;
            int[] rows = new int[H], cols = new int[W];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (P[(y * W) + x]) { rows[y]++; cols[x]++; }
            int r0 = Quantile(rows, total, 0.01), r1 = Quantile(rows, total, 0.99);
            int c0 = Quantile(cols, total, 0.005), c1 = Quantile(cols, total, 0.995);
            int band = Math.Max(1, r1 - r0);
            r0 -= band / 10; r1 += band / 10;
            c0 -= band / 2; c1 += band / 2;

            int[] label;
            List<int> areas = Components(out label);
            int n = areas.Count;
            int[] minX = new int[n], maxX = new int[n], minY = new int[n], maxY = new int[n];
            for (int k = 0; k < n; k++) { minX[k] = minY[k] = int.MaxValue; maxX[k] = maxY[k] = -1; }
            for (int i = 0; i < P.Length; i++)
            {
                int k = label[i];
                if (k < 0) continue;
                int x = i % W, y = i / W;
                if (x < minX[k]) minX[k] = x;
                if (x > maxX[k]) maxX[k] = x;
                if (y < minY[k]) minY[k] = y;
                if (y > maxY[k]) maxY[k] = y;
            }
            bool[] keep = new bool[n];
            for (int k = 0; k < n; k++)
                keep[k] = maxY[k] >= r0 && minY[k] <= r1 && maxX[k] >= c0 && minX[k] <= c1
                          && (maxY[k] - minY[k] >= band / 8 || maxX[k] - minX[k] >= band / 8);
            InkImage o = new InkImage(W, H);
            for (int i = 0; i < P.Length; i++) o.P[i] = P[i] && keep[label[i]];
            return o;
        }

        static int Quantile(int[] counts, int total, double q)
        {
            long want = (long)Math.Ceiling(total * q), acc = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                acc += counts[i];
                if (acc >= want && acc > 0) return i;
            }
            return counts.Length - 1;
        }

        public Rectangle InkBounds()
        {
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (P[(y * W) + x])
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        public InkImage Crop(Rectangle r)
        {
            InkImage o = new InkImage(r.Width, r.Height);
            for (int y = 0; y < o.H; y++)
                for (int x = 0; x < o.W; x++)
                    o.P[(y * o.W) + x] = this[x + r.X, y + r.Y];
            return o;
        }

        public InkImage Flip180()
        {
            InkImage o = new InkImage(W, H);
            for (int i = 0; i < P.Length; i++) o.P[P.Length - 1 - i] = P[i];
            return o;
        }

        // Average letter stroke thickness in pixels: ink area divided by half its outline length.
        public double StrokeWidth()
        {
            int area = 0, edge = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (!P[(y * W) + x]) continue;
                    area++;
                    if (!this[x - 1, y] || !this[x + 1, y] || !this[x, y - 1] || !this[x, y + 1]) edge++;
                }
            return edge == 0 ? 1 : Math.Max(1, 2.0 * area / edge);
        }

        // Area-based resample to w x h (a pixel is ink when at least half of it is covered).
        public InkImage Resize(int w, int h)
        {
            InkImage o = new InkImage(w, h);
            double fx = W / (double)o.W, fy = H / (double)o.H;
            for (int y = 0; y < o.H; y++)
                for (int x = 0; x < o.W; x++)
                {
                    int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, (int)((x + 1) * fx));
                    int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, (int)((y + 1) * fy));
                    int n = 0, t = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) { t++; if (this[xx, yy]) n++; }
                    o.P[(y * o.W) + x] = n * 2 >= t;
                }
            return o;
        }

        public InkImage Dilate(int r) { return r > 0 ? Grow(r, true) : this; }

        // Majority vote in each 3x3 block: smooths the ragged edges scan points leave without making
        // strokes thicker or thinner (ragged edges would otherwise favour bolder fonts).
        public InkImage Smooth(int passes)
        {
            InkImage cur = this;
            for (int p = 0; p < passes; p++)
            {
                InkImage o = new InkImage(W, H);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (cur[x + dx, y + dy]) n++;
                        o.P[(y * W) + x] = n >= 5;
                    }
                cur = o;
            }
            return cur;
        }

        // Black letters on white with a margin, the way OCR engines like it.
        public Bitmap ToBitmap(int margin)
        {
            Bitmap b = new Bitmap(W + (2 * margin), H + (2 * margin), PixelFormat.Format24bppRgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            byte[] raw = new byte[d.Stride * b.Height];
            for (int i = 0; i < raw.Length; i++) raw[i] = 255;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (P[(y * W) + x])
                    {
                        int o = ((y + margin) * d.Stride) + ((x + margin) * 3);
                        raw[o] = raw[o + 1] = raw[o + 2] = 0;
                    }
            Marshal.Copy(raw, 0, d.Scan0, raw.Length);
            b.UnlockBits(d);
            return b;
        }
    }
}
