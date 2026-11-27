using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TextOutline;

// Checks the SHX font reader without AutoCAD: builds small .shx files byte by byte
// (classic "shapes 1.0" and "unifont 1.0") and checks the strokes every shape code draws.
static class ShxTest
{
    static int passed, failed;

    static int Main()
    {
        ShxFont f = ShxFont.Parse(ShapesFile(Shapes(false), "Test font", 10, 2));
        ShxFont u = ShxFont.Parse(UnifontFile(Shapes(true), "Test unifont", 10, 2));

        Check("font info is read", f.Name == "Test font" && f.Above == 10 && f.Below == 2 && !f.IsUnifont);
        Check("unifont info is read", u.Name == "Test unifont" && u.Above == 10 && u.Below == 2 && u.IsUnifont);

        foreach (ShxFont font in new ShxFont[] { f, u })
        {
            string tag = font.IsUnifont ? " (unifont)" : " (shapes)";
            double adv;
            List<ShxStroke> s;

            s = font.RenderShape('L', out adv);
            Check("vectors and pen up/down" + tag, s.Count == 1 && Pts(s[0], 0, 10, 0, 0, 6, 0) && Near(adv, 8));

            s = font.RenderShape('O', out adv);
            bool round = s.Count == 1 && s[0].Count == 5 && s[0].IsClosed && Near(adv, 11);
            if (round)
                for (int i = 0; i < 5; i++)
                {
                    round &= Near(Math.Sqrt(Sq(s[0].X[i] - 3) + Sq(s[0].Y[i] - 5)), 3);
                    if (i < 4) round &= Near(s[0].Bulge[i], Math.Tan(Math.PI / 8));
                }
            Check("full octant circle is a closed 4-bulge polyline" + tag, round);

            s = font.RenderShape('(', out adv);
            Check("bulge arc (code 12)" + tag, s.Count == 1 && Pts(s[0], 0, 0, 0, 10) && Near(s[0].Bulge[0], 1));

            s = font.RenderShape('B', out adv);
            Check("multiple bulge arcs (code 13)" + tag, s.Count == 1 && Pts(s[0], 0, 0, 4, 0, 4, 4)
                  && Near(s[0].Bulge[0], -64 / 127.0) && Near(s[0].Bulge[1], 0));

            s = font.RenderShape('X', out adv);
            Check("subshape (code 7)" + tag, s.Count == 1 && Pts(s[0], 0, 10, 0, 0, 6, 0) && Near(adv, 8));

            s = font.RenderShape('|', out adv);
            Check("vertical-only command is skipped (code 14)" + tag, s.Count == 1 && Pts(s[0], 0, 0, 3, 0) && Near(adv, 3));

            s = font.RenderShape('S', out adv);
            Check("scale up and down (codes 3, 4)" + tag, s.Count == 1 && Pts(s[0], 0, 0, 2, 0, 3, 0));

            s = font.RenderShape('M', out adv);
            Check("multiple displacements (code 9)" + tag, s.Count == 1 && Pts(s[0], 0, 0, 1, 1, 3, 0));

            s = font.RenderShape('P', out adv);
            Check("push and pop (codes 5, 6)" + tag, s.Count == 2 && Pts(s[0], 0, 0, 0, 1) && Pts(s[1], 0, 0, 1, 0));

            double advF, advG;
            List<ShxStroke> fr = font.RenderShape('F', out advF), oc = font.RenderShape('G', out advG);
            Check("fractional arc with zero offsets = octant arc (code 11)" + tag,
                  fr.Count == 1 && oc.Count == 1 && Near(advF, -3) && Near(advG, -3)
                  && Near(Last(fr[0], true), -3) && Near(Last(fr[0], false), 3)
                  && Near(Last(oc[0], true), -3) && Near(Last(oc[0], false), 3));

            // Start 11.25 degrees, end 67.5 degrees, radius 3: centre is 3 units "behind" the start point.
            s = font.RenderShape('H', out adv);
            double cx = -3 * Math.Cos(Rad(11.25)), cy = -3 * Math.Sin(Rad(11.25));
            Check("fractional arc with offsets ends at the right angle" + tag, s.Count == 1
                  && Near(Last(s[0], true), cx + (3 * Math.Cos(Rad(67.5))))
                  && Near(Last(s[0], false), cy + (3 * Math.Sin(Rad(67.5)))));

            s = font.RenderShape('R', out adv);
            Check("clockwise octant arc" + tag, s.Count == 1 && Near(Last(s[0], true), 2) && Near(Last(s[0], false), 2)
                  && s[0].Bulge[0] < 0);

            int missing;
            s = font.Render("LL", out adv, out missing);
            Check("string: glyphs follow each other" + tag, s.Count == 2 && Near(adv, 16) && Near(s[1].X[0], 8) && missing == 0);

            s = font.Render("L?L", out adv, out missing);
            Check("string: missing glyph is counted and spaced" + tag, s.Count == 2 && missing == 1 && Near(adv, 26));

            s = font.Render("°", out adv, out missing);
            Check("degree sign (%%d) is found" + tag, s.Count == 1 && missing == 0);
        }

        double a;
        int m;

        // Flattening (used for stretched or slanted text): points stay on the arc, on the right side.
        ShxStroke half = f.RenderShape('(', out a)[0].Flatten(4);
        bool onArc = half.Count == 9 && Near(half.X[4], 5) && Near(half.Y[4], 5) && Near(half.X[8], 0) && Near(half.Y[8], 10);
        for (int i = 0; i < half.Count; i++) onArc &= Near(Math.Sqrt(Sq(half.X[i]) + Sq(half.Y[i] - 5)), 5) && half.Bulge[i] == 0;
        Check("flattened half circle: 8 pieces on the circle, bulging east", onArc);

        ShxStroke ring = f.RenderShape('O', out a)[0].Flatten(8);
        bool onRing = ring.Count == 33 && ring.IsClosed;
        for (int i = 0; i < ring.Count; i++) onRing &= Near(Math.Sqrt(Sq(ring.X[i] - 3) + Sq(ring.Y[i] - 5)), 3);
        Check("flattened circle stays closed and round", onRing);

        ShxStroke dip = f.RenderShape('B', out a)[0].Flatten(16);
        double top = 0;
        for (int i = 0; i < dip.Count - 1; i++) top = Math.Max(top, dip.Y[i]);   // the arc, without the final straight piece
        double sagitta = (64 / 127.0) * 4 / 2;
        Check("flattened clockwise bulge rises to its sagitta", top > 0.99 * sagitta && top <= sagitta + 1e-9
              && Near(dip.X[dip.Count - 2], 4) && Near(dip.Y[dip.Count - 2], 0) && Near(dip.Y[dip.Count - 1], 4));

        Check("accented letter through code page 1252", f.Render("é", out a, out m).Count == 1 && m == 0);
        Check("unifont glyph above U+00FF", u.Render("Ж", out a, out m).Count == 1 && m == 0);

        bool threw = false;
        try { ShxFont.Parse(Encoding.ASCII.GetBytes("AutoCAD-86 bigfont 1.0\r\n\u001A\0\0\0\0")); }
        catch (InvalidDataException) { threw = true; }
        Check("big font gives a clear error", threw);

        Console.WriteLine();
        Console.WriteLine("{0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ test shapes

    static Dictionary<int, byte[]> Shapes(bool unifont)
    {
        Dictionary<int, byte[]> d = new Dictionary<int, byte[]>();
        d['L'] = B(2, 0xA4, 1, 0xAC, 0x60, 2, 0x20, 0);                  // up 10 (pen up), down 10, east 6, gap 2
        d['O'] = B(2, 8, 6, 5, 1, 10, 3, 0x00, 2, 8, 5, -5, 0);          // full circle r 3 around (3,5)
        d['('] = B(12, 0, 10, 127, 0);                                     // half circle, counter-clockwise: bulges east
        d['B'] = B(13, 4, 0, -64, 0, 4, 0, 0, 0, 0);                       // bulge arc then straight piece
        d['X'] = unifont ? B(7, 0, 'L', 0) : B(7, 'L', 0);                 // subshape
        d['|'] = B(14, 0x24, 0x30, 0);                                     // vertical-only vector, then east 3
        d['S'] = B(4, 2, 0x10, 3, 2, 0x10, 0);                             // x2: 2 units, /2: 1 unit
        d['M'] = B(9, 1, 1, 2, -1, 0, 0, 0);
        d['P'] = B(5, 0x14, 6, 0x10, 0);
        d['F'] = B(11, 0, 0, 0, 3, 0x02, 0);                               // fractional, 2 octants from 0
        d['G'] = B(10, 3, 0x02, 0);                                        // octant arc, 2 octants from 0
        d['H'] = B(11, 64, 128, 0, 3, 0x02, 0);                            // 11.25 to 67.5 degrees
        d['R'] = B(10, 2, 0x80 | 0x42, 0);                                 // clockwise 180 -> 90 degrees, centre (2,0)
        d[unifont ? 0x00B0 : 127] = B(0x14, 0);                            // degree sign
        if (unifont) d[0x0416] = B(0x14, 0);                               // Cyrillic Zhe
        else d[0xE9] = B(0x14, 0);                                         // e-acute in code page 1252
        return d;
    }

    static byte[] B(params int[] v)
    {
        byte[] r = new byte[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = unchecked((byte)v[i]);
        return r;
    }

    // ------------------------------------------------------------------ SHX writers

    static byte[] Def(string name, byte[] bytes)
    {
        MemoryStream m = new MemoryStream();
        byte[] n = Encoding.ASCII.GetBytes(name);
        m.Write(n, 0, n.Length);
        m.WriteByte(0);
        m.Write(bytes, 0, bytes.Length);
        return m.ToArray();
    }

    static byte[] ShapesFile(Dictionary<int, byte[]> shapes, string name, int above, int below)
    {
        SortedDictionary<int, byte[]> defs = new SortedDictionary<int, byte[]>();
        defs[0] = Def(name, B(above, below, 0, 0));
        foreach (KeyValuePair<int, byte[]> kv in shapes) defs[kv.Key] = Def("", kv.Value);

        BinaryWriter w = new BinaryWriter(new MemoryStream());
        w.Write(Encoding.ASCII.GetBytes("AutoCAD-86 shapes 1.0\r\n\u001A"));
        int first = -1, last = 0;
        foreach (int k in defs.Keys) { if (first < 0) first = k; last = k; }
        w.Write((ushort)first);
        w.Write((ushort)last);
        w.Write((ushort)defs.Count);
        foreach (KeyValuePair<int, byte[]> kv in defs) { w.Write((ushort)kv.Key); w.Write((ushort)kv.Value.Length); }
        foreach (byte[] def in defs.Values) w.Write(def);
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    static byte[] UnifontFile(Dictionary<int, byte[]> shapes, string name, int above, int below)
    {
        BinaryWriter w = new BinaryWriter(new MemoryStream());
        w.Write(Encoding.ASCII.GetBytes("AutoCAD-86 unifont 1.0\r\n\u001A"));
        w.Write((uint)(shapes.Count + 1));
        byte[] info = Def(name, B(above, below, 0, 0, 0, 0));
        w.Write((ushort)info.Length);
        w.Write(info);
        foreach (KeyValuePair<int, byte[]> kv in shapes)
        {
            byte[] def = Def("", kv.Value);
            w.Write((ushort)kv.Key);
            w.Write((ushort)def.Length);
            w.Write(def);
        }
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    // ------------------------------------------------------------------ helpers

    static bool Pts(ShxStroke s, params double[] xy)
    {
        if (s.Count * 2 != xy.Length) return false;
        for (int i = 0; i < s.Count; i++)
            if (!Near(s.X[i], xy[2 * i]) || !Near(s.Y[i], xy[(2 * i) + 1])) return false;
        return true;
    }

    static double Last(ShxStroke s, bool x) { return x ? s.X[s.Count - 1] : s.Y[s.Count - 1]; }
    static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-6; }
    static double Sq(double v) { return v * v; }
    static double Rad(double deg) { return deg * Math.PI / 180; }

    static void Check(string name, bool ok)
    {
        Console.WriteLine("{0}  {1}", ok ? "PASS" : "FAIL", name);
        if (ok) passed++; else failed++;
    }
}
