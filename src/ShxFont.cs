using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TextOutline
{
    // One pen-down stroke of an SHX glyph: a polyline in shape units (y up, baseline at y = 0).
    // Bulge[i] belongs to the segment from vertex i to vertex i + 1, as in an AutoCAD polyline.
    public class ShxStroke
    {
        public List<double> X = new List<double>();
        public List<double> Y = new List<double>();
        public List<double> Bulge = new List<double>();

        public int Count { get { return X.Count; } }

        public bool IsClosed
        {
            get
            {
                int n = X.Count;
                return n > 2 && Math.Abs(X[0] - X[n - 1]) < 1e-9 && Math.Abs(Y[0] - Y[n - 1]) < 1e-9;
            }
        }

        // Copy with every bulge segment replaced by straight pieces, perQuarter per 90 degrees.
        // Needed when the text is stretched or slanted: an arc then becomes part of an ellipse.
        public ShxStroke Flatten(int perQuarter)
        {
            ShxStroke r = new ShxStroke();
            for (int i = 0; i < Count; i++)
            {
                r.X.Add(X[i]); r.Y.Add(Y[i]); r.Bulge.Add(0);
                if (i + 1 >= Count || Bulge[i] == 0) continue;

                double x0 = X[i], y0 = Y[i], x1 = X[i + 1], y1 = Y[i + 1];
                double dx = x1 - x0, dy = y1 - y0, c = Math.Sqrt((dx * dx) + (dy * dy));
                if (c < 1e-12) continue;
                double theta = 4 * Math.Atan(Bulge[i]);                 // included angle, negative = clockwise
                double rad = c / (2 * Math.Sin(theta / 2));             // signed like theta
                double h = rad * Math.Cos(theta / 2);                   // centre's distance from the chord
                double cx = ((x0 + x1) / 2) - (dy / c * h), cy = ((y0 + y1) / 2) + (dx / c * h);
                double a0 = Math.Atan2(y0 - cy, x0 - cx), r0 = Math.Abs(rad);
                int pieces = Math.Max(2, (int)Math.Ceiling((Math.Abs(theta) / (Math.PI / 2) * perQuarter) - 1e-9));
                for (int k = 1; k < pieces; k++)
                {
                    double a = a0 + (theta * k / pieces);
                    r.X.Add(cx + (r0 * Math.Cos(a))); r.Y.Add(cy + (r0 * Math.Sin(a))); r.Bulge.Add(0);
                }
            }
            return r;
        }
    }

    // Reads compiled AutoCAD shape fonts (.shx): "shapes 1.0/1.1" and "unifont 1.0" files.
    // SHX letters are single strokes, so a glyph becomes open (or closed) polylines along those
    // strokes, the same lines AutoCAD draws on screen. No AutoCAD dependency.
    //
    // Shape bytes follow the AutoCAD shape description format: a byte with a non-zero high nibble is a
    // vector (length = high nibble, direction = low nibble); bytes 0-14 are special codes (pen up/down,
    // scale, push/pop, subshape, displacements, octant arcs, fractional arcs, bulge arcs, vertical-only).
    public class ShxFont
    {
        public string Name = "";
        public bool IsUnifont;
        // Height of capital letters above the baseline, and depth of descenders, in shape units.
        public int Above, Below;

        readonly Dictionary<int, byte[]> shapes = new Dictionary<int, byte[]>();

        static readonly Dictionary<string, ShxFont> cache = new Dictionary<string, ShxFont>(StringComparer.OrdinalIgnoreCase);

        public int ShapeCount { get { return shapes.Count; } }

        public static ShxFont Load(string path)
        {
            ShxFont font;
            if (cache.TryGetValue(path, out font)) return font;
            font = Parse(File.ReadAllBytes(path));
            cache[path] = font;
            return font;
        }

        // ------------------------------------------------------------------ file parsing

        public static ShxFont Parse(byte[] data)
        {
            int eof = Array.IndexOf(data, (byte)0x1A);
            if (eof < 0 || eof > 64) throw new InvalidDataException("Not an SHX file (no header).");
            string header = Encoding.ASCII.GetString(data, 0, eof).Trim();
            int p = eof + 1;
            ShxFont f = new ShxFont();

            if (header.StartsWith("AutoCAD-86 shapes", StringComparison.OrdinalIgnoreCase))
            {
                U16(data, ref p);                       // first shape number
                U16(data, ref p);                       // last shape number
                int count = U16(data, ref p);
                int[] numbers = new int[count], lengths = new int[count];
                for (int i = 0; i < count; i++)
                {
                    numbers[i] = U16(data, ref p);
                    lengths[i] = U16(data, ref p);
                }
                for (int i = 0; i < count; i++)
                {
                    byte[] def = Slice(data, p, lengths[i]);
                    p += lengths[i];
                    if (numbers[i] == 0) f.ReadInfo(def);
                    else f.shapes[numbers[i]] = SkipName(def);
                }
            }
            else if (header.StartsWith("AutoCAD-86 unifont", StringComparison.OrdinalIgnoreCase))
            {
                f.IsUnifont = true;
                U32(data, ref p);                       // shape count (read to the end of the file instead)
                int infoLength = U16(data, ref p);
                f.ReadInfo(Slice(data, p, infoLength));
                p += infoLength;
                while (p + 4 <= data.Length)
                {
                    int number = U16(data, ref p);
                    int length = U16(data, ref p);
                    if (p + length > data.Length) break;
                    f.shapes[number] = SkipName(Slice(data, p, length));
                    p += length;
                }
            }
            else if (header.StartsWith("AutoCAD-86 bigfont", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Big fonts (Asian SHX) are not supported.");
            }
            else
            {
                throw new InvalidDataException("Unknown SHX type: " + header);
            }

            if (f.Above <= 0) f.Above = f.GuessAbove();
            return f;
        }

        // Font information: name, then above, below, modes (unifont adds encoding and type).
        void ReadInfo(byte[] def)
        {
            int z = Array.IndexOf(def, (byte)0);
            if (z < 0) z = def.Length;
            Name = Encoding.ASCII.GetString(def, 0, z);
            if (z + 2 < def.Length)
            {
                Above = def[z + 1];
                Below = def[z + 2];
            }
        }

        static byte[] SkipName(byte[] def)
        {
            int z = Array.IndexOf(def, (byte)0);
            return z < 0 ? def : Slice(def, z + 1, def.Length - z - 1);
        }

        // Shape files without font information: use the height of "A" (or of anything).
        int GuessAbove()
        {
            int code = shapes.ContainsKey('A') ? 'A' : -1;
            if (code < 0) foreach (int k in shapes.Keys) { code = k; break; }
            if (code < 0) return 1;
            double adv;
            double top = 0;
            foreach (ShxStroke s in RenderShape(code, out adv))
                foreach (double y in s.Y) top = Math.Max(top, y);
            return top > 0 ? (int)Math.Ceiling(top) : 1;
        }

        static int U16(byte[] d, ref int p)
        {
            if (p + 2 > d.Length) throw new InvalidDataException("SHX file is truncated.");
            int v = d[p] | (d[p + 1] << 8);
            p += 2;
            return v;
        }

        static uint U32(byte[] d, ref int p)
        {
            if (p + 4 > d.Length) throw new InvalidDataException("SHX file is truncated.");
            uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            p += 4;
            return v;
        }

        static byte[] Slice(byte[] d, int start, int length)
        {
            if (start < 0 || length < 0 || start + length > d.Length) throw new InvalidDataException("SHX file is truncated.");
            byte[] r = new byte[length];
            Buffer.BlockCopy(d, start, r, 0, length);
            return r;
        }

        // ------------------------------------------------------------------ characters

        static readonly Encoding Ansi = MakeAnsi();

        // Windows-1252 for non-Unicode SHX fonts. .NET 8+ only has it once a code-page provider is
        // registered, so fall back to Latin-1, which matches it except for 0x80-0x9F.
        static Encoding MakeAnsi()
        {
            try { return Encoding.GetEncoding(1252); }
            catch (Exception) { }
            try { return Encoding.GetEncoding(28591); }
            catch (Exception) { return Encoding.ASCII; }
        }

        // Shape number for a character, or -1 if the font has no shape for it.
        public int ShapeFor(char c)
        {
            if (IsUnifont)
            {
                if (shapes.ContainsKey(c)) return c;
            }
            else
            {
                if (c < 128 && shapes.ContainsKey(c)) return c;
                byte[] b = Ansi.GetBytes(new string(c, 1));
                if (b.Length == 1 && b[0] != (byte)'?' && shapes.ContainsKey(b[0])) return b[0];
            }
            // AutoCAD's own slots for %%d, %%p and %%c in SHX fonts.
            int special = c == '°' ? 127 : c == '±' ? 128 : (c == 'Ø' || c == '⌀') ? 129 : -1;
            if (special > 0 && shapes.ContainsKey(special)) return special;
            return -1;
        }

        // Draws a whole string. Returns its strokes in shape units; advance is where the pen ends.
        public List<ShxStroke> Render(string text, out double advance, out int missing)
        {
            List<ShxStroke> all = new List<ShxStroke>();
            double x = 0;
            missing = 0;
            foreach (char c in text)
            {
                int code = ShapeFor(c);
                if (code < 0)
                {
                    if (c != ' ' && c != '\t') missing++;
                    x += Above * (c == ' ' ? 0.5 : 1.0);
                    continue;
                }
                double adv;
                foreach (ShxStroke s in RenderShape(code, out adv))
                {
                    for (int i = 0; i < s.Count; i++) s.X[i] += x;
                    all.Add(s);
                }
                x += adv;
            }
            advance = x;
            return all;
        }

        public List<ShxStroke> RenderShape(int code, out double advance)
        {
            Pen pen = new Pen();
            Run(code, pen, 0);
            pen.EndStroke();
            advance = pen.X;
            return pen.Strokes;
        }

        // ------------------------------------------------------------------ shape interpreter

        // Pen state while a shape is drawn.
        class Pen
        {
            public double X, Y, Scale = 1;
            public bool Down = true;
            public readonly Stack<double[]> Saved = new Stack<double[]>();
            public readonly List<ShxStroke> Strokes = new List<ShxStroke>();
            ShxStroke cur;

            public void EndStroke()
            {
                if (cur != null && cur.Count >= 2) Strokes.Add(cur);
                cur = null;
            }

            // Moves the pen to (nx, ny); draws the segment (with a bulge) if the pen is down.
            public void MoveTo(double nx, double ny, double bulge)
            {
                if (Down)
                {
                    if (cur == null)
                    {
                        cur = new ShxStroke();
                        cur.X.Add(X); cur.Y.Add(Y); cur.Bulge.Add(0);
                    }
                    cur.Bulge[cur.Count - 1] = bulge;
                    cur.X.Add(nx); cur.Y.Add(ny); cur.Bulge.Add(0);
                }
                else
                {
                    EndStroke();
                }
                X = nx;
                Y = ny;
            }

            public void Jump(double nx, double ny)
            {
                EndStroke();
                X = nx;
                Y = ny;
            }
        }

        // Direction vectors for the 16 vector directions (0 = east, 4 = north, counter-clockwise).
        static readonly double[] DirX = { 1, 1, 1, 0.5, 0, -0.5, -1, -1, -1, -1, -1, -0.5, 0, 0.5, 1, 1 };
        static readonly double[] DirY = { 0, 0.5, 1, 1, 1, 1, 1, 0.5, 0, -0.5, -1, -1, -1, -1, -1, -0.5 };

        void Run(int code, Pen pen, int depth)
        {
            byte[] b;
            if (depth > 8 || !shapes.TryGetValue(code, out b)) return;
            int i = 0;
            bool skipNext = false;
            while (i < b.Length)
            {
                bool draw = !skipNext;
                skipNext = false;
                int op = b[i++];
                if (op == 0) break;

                if (op >= 0x10)
                {
                    int len = op >> 4, dir = op & 0x0F;
                    if (draw) pen.MoveTo(pen.X + (len * DirX[dir] * pen.Scale), pen.Y + (len * DirY[dir] * pen.Scale), 0);
                    continue;
                }

                switch (op)
                {
                    case 1: if (draw) pen.Down = true; break;
                    case 2: if (draw) { pen.Down = false; pen.EndStroke(); } break;
                    case 3:
                        { int n = Byte(b, ref i); if (draw && n != 0) pen.Scale /= n; }
                        break;
                    case 4:
                        { int n = Byte(b, ref i); if (draw) pen.Scale *= n; }
                        break;
                    case 5:
                        if (draw && pen.Saved.Count < 16) pen.Saved.Push(new double[] { pen.X, pen.Y });
                        break;
                    case 6:
                        if (draw && pen.Saved.Count > 0) { double[] s = pen.Saved.Pop(); pen.Jump(s[0], s[1]); }
                        break;
                    case 7:
                        {
                            int sub = IsUnifont ? (Byte(b, ref i) << 8) | Byte(b, ref i) : Byte(b, ref i);
                            if (draw)
                            {
                                double keepScale = pen.Scale;
                                bool keepDown = pen.Down;
                                pen.Scale = 1;
                                pen.Down = true;
                                Run(sub, pen, depth + 1);
                                pen.Scale = keepScale;
                                pen.Down = keepDown;
                            }
                        }
                        break;
                    case 8:
                        {
                            int dx = SByte(b, ref i), dy = SByte(b, ref i);
                            if (draw) pen.MoveTo(pen.X + (dx * pen.Scale), pen.Y + (dy * pen.Scale), 0);
                        }
                        break;
                    case 9:
                        while (i + 1 < b.Length)
                        {
                            int dx = SByte(b, ref i), dy = SByte(b, ref i);
                            if (dx == 0 && dy == 0) break;
                            if (draw) pen.MoveTo(pen.X + (dx * pen.Scale), pen.Y + (dy * pen.Scale), 0);
                        }
                        break;
                    case 10:
                        {
                            int radius = Byte(b, ref i);
                            int oc = Byte(b, ref i);
                            if (draw) OctantArc(pen, radius * pen.Scale, oc);
                        }
                        break;
                    case 11:
                        {
                            int startOff = Byte(b, ref i), endOff = Byte(b, ref i);
                            int radius = (Byte(b, ref i) << 8) | Byte(b, ref i);
                            int oc = Byte(b, ref i);
                            if (draw) FractionalArc(pen, radius * pen.Scale, startOff, endOff, oc);
                        }
                        break;
                    case 12:
                        {
                            int dx = SByte(b, ref i), dy = SByte(b, ref i), bulge = SByte(b, ref i);
                            if (draw) BulgeArc(pen, dx, dy, bulge);
                        }
                        break;
                    case 13:
                        while (i + 1 < b.Length)
                        {
                            int dx = SByte(b, ref i), dy = SByte(b, ref i);
                            if (dx == 0 && dy == 0) break;
                            int bulge = SByte(b, ref i);
                            if (draw) BulgeArc(pen, dx, dy, bulge);
                        }
                        break;
                    case 14:
                        // The next command is only for vertical text: skip it for normal text.
                        if (draw) skipNext = true;
                        break;
                    default:
                        break;
                }
            }
        }

        static int Byte(byte[] b, ref int i) { return i < b.Length ? b[i++] : 0; }

        static int SByte(byte[] b, ref int i) { return i < b.Length ? (sbyte)b[i++] : 0; }

        // Octant byte: bit 7 = clockwise, bits 4-6 = start octant, bits 0-2 = octants spanned (0 = 8).
        static void OctantArc(Pen pen, double r, int oc)
        {
            bool cw = (oc & 0x80) != 0;
            int start = (oc >> 4) & 7, span = oc & 7;
            if (span == 0) span = 8;
            double a0 = start * Math.PI / 4;
            double sweep = span * Math.PI / 4 * (cw ? -1 : 1);
            Arc(pen, r, a0, sweep);
        }

        // Like an octant arc, but starting and ending between octant boundaries.
        // Offsets are in 1/256 of an octant and are measured counter-clockwise from the boundary.
        static void FractionalArc(Pen pen, double r, int startOff, int endOff, int oc)
        {
            bool cw = (oc & 0x80) != 0;
            int start = (oc >> 4) & 7, span = oc & 7;
            if (span == 0) span = 8;
            const double oct = Math.PI / 4;
            double a0 = (start * oct) + (startOff * oct / 256.0);
            double a1;
            if (!cw) a1 = endOff == 0 ? (start + span) * oct : ((start + span - 1) * oct) + (endOff * oct / 256.0);
            else a1 = endOff == 0 ? (start - span) * oct : ((start - span) * oct) + (endOff * oct / 256.0);
            double sweep = a1 - a0;
            if (!cw) { while (sweep <= 0) sweep += 2 * Math.PI; }
            else { while (sweep >= 0) sweep -= 2 * Math.PI; }
            Arc(pen, r, a0, sweep);
        }

        // Arc that starts at the pen, on a circle of radius r, at angle a0 measured from its centre.
        static void Arc(Pen pen, double r, double a0, double sweep)
        {
            if (r <= 0) return;
            double cx = pen.X - (r * Math.Cos(a0)), cy = pen.Y - (r * Math.Sin(a0));
            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
            double step = sweep / pieces;
            double bulge = Math.Tan(step / 4);
            for (int k = 1; k <= pieces; k++)
            {
                double a = a0 + (step * k);
                double nx = cx + (r * Math.Cos(a)), ny = cy + (r * Math.Sin(a));
                if (k == pieces && Math.Abs(Math.Abs(sweep) - (2 * Math.PI)) < 1e-9)
                {
                    // A full circle ends exactly where it started, so the stroke closes.
                    nx = cx + (r * Math.Cos(a0));
                    ny = cy + (r * Math.Sin(a0));
                }
                pen.MoveTo(nx, ny, bulge);
            }
        }

        // Code 12/13 arc: chord (dx, dy) with bulge/127 = 2 * height / chord (same as a polyline bulge).
        static void BulgeArc(Pen pen, int dx, int dy, int bulge)
        {
            pen.MoveTo(pen.X + (dx * pen.Scale), pen.Y + (dy * pen.Scale), bulge / 127.0);
        }
    }
}
