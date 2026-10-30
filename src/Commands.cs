using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Region = Autodesk.AutoCAD.DatabaseServices.Region;

[assembly: ExtensionApplication(typeof(TextOutline.Plugin))]
[assembly: CommandClass(typeof(TextOutline.Commands))]

namespace TextOutline
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage("\nText to Outline loaded. Type TEXTOUTLINE (or TXO) and select text.\n");
        }

        public void Terminate() { }
    }

    public class Commands
    {
        static bool deleteOriginal = false;

        [CommandMethod("TEXTOUTLINE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TextOutlineCommand() { Run(); }

        [CommandMethod("TXO", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TxoCommand() { Run(); }

        [CommandMethod("TXOQUALITY", CommandFlags.Modal)]
        public void QualityCommand()
        {
            Editor ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
            PromptKeywordOptions pko = new PromptKeywordOptions("\nCurve smoothness");
            pko.Keywords.Add("Draft");
            pko.Keywords.Add("Normal");
            pko.Keywords.Add("Fine");
            pko.Keywords.Default = GlyphOutline.Flatness >= 1f ? "Draft" : GlyphOutline.Flatness <= 0.05f ? "Fine" : "Normal";
            pko.AllowNone = true;
            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK) return;
            if (pr.StringResult == "Draft") GlyphOutline.Flatness = 1f;
            else if (pr.StringResult == "Fine") GlyphOutline.Flatness = 0.05f;
            else GlyphOutline.Flatness = 0.2f;
            ed.WriteMessage("\nCurve smoothness set to {0}.", pr.StringResult);
        }

        static void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nSelect text to convert to outline polylines: ";
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "TEXT,MTEXT") });
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK) return;

            PromptKeywordOptions pko = new PromptKeywordOptions("\nDelete original text?");
            pko.Keywords.Add("Yes");
            pko.Keywords.Add("No");
            pko.Keywords.Default = deleteOriginal ? "Yes" : "No";
            pko.AllowNone = true;
            PromptResult pkr = ed.GetKeywords(pko);
            if (pkr.Status != PromptStatus.OK && pkr.Status != PromptStatus.None) return;
            if (pkr.Status == PromptStatus.OK) deleteOriginal = pkr.StringResult == "Yes";

            Report report = new Report();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    Entity ent = (Entity)tr.GetObject(so.ObjectId, OpenMode.ForRead);
                    int before = report.Polylines;
                    bool allOk = true;

                    if (ent is MText)
                    {
                        DBObjectCollection parts = new DBObjectCollection();
                        ((MText)ent).Explode(parts);
                        foreach (DBObject part in parts)
                        {
                            DBText piece = part as DBText;
                            if (piece != null && !Converter.Convert(piece, ent, space, tr, report)) allOk = false;
                            part.Dispose();
                        }
                    }
                    else if (ent is DBText)
                    {
                        allOk = Converter.Convert((DBText)ent, ent, space, tr, report);
                    }

                    if (deleteOriginal && allOk && report.Polylines > before)
                    {
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }
                tr.Commit();
            }

            ed.WriteMessage("\nCreated {0} outline polyline(s) from {1} text object(s).", report.Polylines, report.Texts);
            if (report.ShxSkipped > 0)
                ed.WriteMessage("\nSkipped {0} text(s) using SHX fonts ({1}). SHX fonts are single-stroke and have no outline; switch the text style to a TrueType font.",
                    report.ShxSkipped, string.Join(", ", report.ShxFonts.ToArray()));
            if (report.Failed > 0)
                ed.WriteMessage("\n{0} text(s) could not be converted: {1}", report.Failed, report.LastError);
            if (report.SizeWarnings > 0)
                ed.WriteMessage("\nNote: {0} text(s) gave an outline whose proportions differ from the text by more than 3%. Please check them.", report.SizeWarnings);
            ed.WriteMessage("\n");
        }
    }

    public class Report
    {
        public int Texts, Polylines, ShxSkipped, Failed, SizeWarnings;
        public List<string> ShxFonts = new List<string>();
        public string LastError = "";
    }

    static class Converter
    {
        // Converts one single-line text into outline polylines. Returns false if it could not be converted.
        public static bool Convert(DBText txt, Entity owner, BlockTableRecord space, Transaction tr, Report report)
        {
            Database db = space.Database;
            try
            {
                string str = TextCodes.Decode(txt.TextString);
                if (str.Trim().Length == 0) return true;

                TextStyleTableRecord tsr = (TextStyleTableRecord)tr.GetObject(txt.TextStyleId, OpenMode.ForRead);
                FontStyle style;
                FontFamily family = ResolveFont(tsr, db, out style);
                if (family == null)
                {
                    report.ShxSkipped++;
                    string name = Path.GetFileName(tsr.FileName);
                    if (!report.ShxFonts.Contains(name)) report.ShxFonts.Add(name);
                    return false;
                }

                // 1. Glyph outlines in font units
                List<Contour> contours = GlyphOutline.GetContours(str, family, style);
                if (contours.Count == 0) return true;

                // 2. Slant (oblique angle) and mirroring
                double tanObl = Math.Tan(txt.Oblique);
                double mx = txt.IsMirroredInX ? -1 : 1;
                double my = txt.IsMirroredInY ? -1 : 1;
                foreach (Contour c in contours)
                    for (int i = 0; i < c.Count; i++)
                    {
                        double y = c.Y[i];
                        c.X[i] = mx * (c.X[i] + (y * tanObl));
                        c.Y[i] = my * y;
                    }

                // 3. Merge: remove overlaps and internal lines, keep outer edges and holes
                List<List<Point2d>> loops = Outline.Merge(contours);
                if (loops.Count == 0) return true;

                // 4. Fit the outline onto the AutoCAD text, in the text's own coordinate system
                Matrix3d toWorld = Matrix3d.Displacement(txt.Position.GetAsVector())
                                 * Matrix3d.PlaneToWorld(txt.Normal)
                                 * Matrix3d.Rotation(txt.Rotation, Vector3d.ZAxis, Point3d.Origin);
                Extents3d target = LocalExtents(txt, toWorld, space, tr);

                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (List<Point2d> loop in loops)
                    foreach (Point2d p in loop)
                    {
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }
                double w = maxX - minX, h = maxY - minY;
                double tw = target.MaxPoint.X - target.MinPoint.X, th = target.MaxPoint.Y - target.MinPoint.Y;
                if (w <= 0 || h <= 0 || tw <= 0 || th <= 0) return true;
                double sx = tw / w, sy = th / h;

                bool stretched = txt.Justify == AttachmentPoint.BaseFit || txt.Justify == AttachmentPoint.BaseAlign;
                if (!stretched && txt.WidthFactor > 0)
                {
                    double ratio = (sx / txt.WidthFactor) / sy;
                    if (Math.Abs(ratio - 1) > 0.03) report.SizeWarnings++;
                }

                // 5. Create the closed polylines
                foreach (List<Point2d> loop in loops)
                {
                    Polyline pl = new Polyline(loop.Count);
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Point2d p = loop[i];
                        pl.AddVertexAt(i, new Point2d(target.MinPoint.X + ((p.X - minX) * sx),
                                                      target.MinPoint.Y + ((p.Y - minY) * sy)), 0, 0, 0);
                    }
                    pl.Closed = true;
                    pl.TransformBy(toWorld);
                    pl.SetPropertiesFrom(owner);
                    space.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    report.Polylines++;
                }
                report.Texts++;
                return true;
            }
            catch (System.Exception ex)
            {
                report.Failed++;
                report.LastError = ex.Message;
                return false;
            }
        }

        // Extents of the text measured in its own (unrotated) coordinate system.
        static Extents3d LocalExtents(DBText txt, Matrix3d toWorld, BlockTableRecord space, Transaction tr)
        {
            DBText probe = (DBText)txt.Clone();
            probe.TransformBy(toWorld.Inverse());
            space.AppendEntity(probe);
            tr.AddNewlyCreatedDBObject(probe, true);
            Extents3d ext = probe.GeometricExtents;
            probe.Erase();
            return ext;
        }

        static FontFamily ResolveFont(TextStyleTableRecord tsr, Database db, out FontStyle style)
        {
            Autodesk.AutoCAD.GraphicsInterface.FontDescriptor fd = tsr.Font;
            style = FontStyle.Regular;
            if (fd.Bold) style |= FontStyle.Bold;
            if (fd.Italic) style |= FontStyle.Italic;

            FontFamily family = GlyphOutline.FamilyFromName(fd.TypeFace);
            if (family != null) return family;

            string file = tsr.FileName;
            string ext = Path.GetExtension(file ?? "").ToLowerInvariant();
            if (ext != ".ttf" && ext != ".otf" && ext != ".ttc") return null;

            string path = null;
            try { path = HostApplicationServices.Current.FindFile(file, db, FindFileHint.TrueTypeFontFile); }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), Path.GetFileName(file));
            return GlyphOutline.FamilyFromFile(path);
        }
    }

    static class Outline
    {
        // Unites the solid contours and cuts the holes (largest first) using AutoCAD regions,
        // then returns the boundary of the result as closed point loops.
        public static List<List<Point2d>> Merge(List<Contour> contours)
        {
            Region acc = null;
            try
            {
                foreach (Contour c in GlyphOutline.MergeOrder(contours))
                {
                    Region r = MakeRegion(c);
                    if (r == null) continue;
                    if (acc == null)
                    {
                        if (c.IsHole) r.Dispose();
                        else acc = r;
                        continue;
                    }
                    acc.BooleanOperation(c.IsHole ? BooleanOperationType.BoolSubtract : BooleanOperationType.BoolUnite, r);
                    r.Dispose();
                }

                List<Point2d[]> segs = new List<Point2d[]>();
                if (acc != null && acc.Area > 0) CollectSegments(acc, segs);
                return Chain(segs, 1e-4);
            }
            finally
            {
                if (acc != null) acc.Dispose();
            }
        }

        static Region MakeRegion(Contour c)
        {
            Polyline pl = new Polyline(c.Count);
            for (int i = 0; i < c.Count; i++) pl.AddVertexAt(i, new Point2d(c.X[i], c.Y[i]), 0, 0, 0);
            pl.Closed = true;
            DBObjectCollection curves = new DBObjectCollection();
            curves.Add(pl);
            try
            {
                DBObjectCollection regions = Region.CreateFromCurves(curves);
                Region first = null;
                foreach (DBObject o in regions)
                {
                    if (first == null) first = (Region)o;
                    else o.Dispose();
                }
                return first;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
            finally
            {
                pl.Dispose();
            }
        }

        static void CollectSegments(Entity ent, List<Point2d[]> segs)
        {
            DBObjectCollection parts = new DBObjectCollection();
            ent.Explode(parts);
            foreach (DBObject o in parts)
            {
                if (o is Region)
                {
                    CollectSegments((Region)o, segs);
                }
                else if (o is Line)
                {
                    Line l = (Line)o;
                    segs.Add(new Point2d[] { new Point2d(l.StartPoint.X, l.StartPoint.Y), new Point2d(l.EndPoint.X, l.EndPoint.Y) });
                }
                else if (o is Curve)
                {
                    Curve cv = (Curve)o;
                    const int n = 32;
                    Point3d prev = cv.StartPoint;
                    for (int i = 1; i <= n; i++)
                    {
                        Point3d p = cv.GetPointAtParameter(cv.StartParam + ((cv.EndParam - cv.StartParam) * i / n));
                        segs.Add(new Point2d[] { new Point2d(prev.X, prev.Y), new Point2d(p.X, p.Y) });
                        prev = p;
                    }
                }
                o.Dispose();
            }
        }

        // Joins loose segments end-to-end into closed loops (the JOIN step).
        public static List<List<Point2d>> Chain(List<Point2d[]> segs, double tol)
        {
            VertexIndex vi = new VertexIndex(tol);
            List<int[]> edges = new List<int[]>();
            foreach (Point2d[] s in segs)
            {
                int a = vi.Get(s[0]), b = vi.Get(s[1]);
                if (a != b) edges.Add(new int[] { a, b });
            }

            List<List<int>> adj = new List<List<int>>();
            for (int i = 0; i < vi.Points.Count; i++) adj.Add(new List<int>());
            for (int e = 0; e < edges.Count; e++)
            {
                adj[edges[e][0]].Add(e);
                adj[edges[e][1]].Add(e);
            }

            bool[] used = new bool[edges.Count];
            List<List<Point2d>> loops = new List<List<Point2d>>();
            for (int e0 = 0; e0 < edges.Count; e0++)
            {
                if (used[e0]) continue;
                used[e0] = true;
                int start = edges[e0][0];
                int cur = edges[e0][1];
                List<int> loop = new List<int>();
                loop.Add(start);
                while (cur != start)
                {
                    loop.Add(cur);
                    int next = -1;
                    foreach (int e in adj[cur])
                        if (!used[e]) { next = e; break; }
                    if (next < 0) break;
                    used[next] = true;
                    cur = edges[next][0] == cur ? edges[next][1] : edges[next][0];
                }
                if (cur != start) continue;

                List<Point2d> pts = new List<Point2d>();
                foreach (int v in loop) pts.Add(vi.Points[v]);
                RemoveCollinear(pts);
                if (pts.Count >= 3) loops.Add(pts);
            }
            return loops;
        }

        static void RemoveCollinear(List<Point2d> pts)
        {
            bool changed = true;
            while (changed && pts.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count && pts.Count >= 3; i++)
                {
                    Point2d a = pts[(i + pts.Count - 1) % pts.Count], b = pts[i], c = pts[(i + 1) % pts.Count];
                    Vector2d u = b - a, v = c - b;
                    double cross = (u.X * v.Y) - (u.Y * v.X);
                    if (Math.Abs(cross) <= 1e-9 * u.Length * v.Length || u.Length < 1e-9 || v.Length < 1e-9)
                    {
                        pts.RemoveAt(i);
                        i--;
                        changed = true;
                    }
                }
            }
        }

        class VertexIndex
        {
            readonly double tol, cell;
            readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            public readonly List<Point2d> Points = new List<Point2d>();

            public VertexIndex(double tol) { this.tol = tol; cell = tol * 4; }

            public int Get(Point2d p)
            {
                long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        List<int> list;
                        if (grid.TryGetValue(Key(cx + dx, cy + dy), out list))
                            foreach (int i in list)
                                if (Points[i].GetDistanceTo(p) <= tol) return i;
                    }
                Points.Add(p);
                long k = Key(cx, cy);
                List<int> bucket;
                if (!grid.TryGetValue(k, out bucket)) { bucket = new List<int>(); grid[k] = bucket; }
                bucket.Add(Points.Count - 1);
                return Points.Count - 1;
            }

            static long Key(long x, long y) { return (x * 73856093L) ^ (y * 19349663L); }
        }
    }

    static class TextCodes
    {
        // Turns AutoCAD control codes (%%d, %%p, %%c, %%nnn, \U+XXXX) into real characters.
        public static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && s[i + 1] == '%')
                {
                    char c = char.ToLowerInvariant(s[i + 2]);
                    if (c == 'd') { sb.Append('°'); i += 2; continue; }
                    if (c == 'p') { sb.Append('±'); i += 2; continue; }
                    if (c == 'c') { sb.Append('Ø'); i += 2; continue; }
                    if (c == '%') { sb.Append('%'); i += 2; continue; }
                    if (c == 'u' || c == 'o') { i += 2; continue; }
                    if (char.IsDigit(c))
                    {
                        int j = i + 2, code = 0;
                        while (j < s.Length && j < i + 5 && char.IsDigit(s[j])) code = (code * 10) + (s[j++] - '0');
                        sb.Append((char)code);
                        i = j - 1;
                        continue;
                    }
                }
                if (s[i] == '\\' && i + 6 < s.Length && (s[i + 1] == 'U' || s[i + 1] == 'u') && s[i + 2] == '+')
                {
                    int code;
                    if (int.TryParse(s.Substring(i + 3, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                    {
                        sb.Append((char)code);
                        i += 6;
                        continue;
                    }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
