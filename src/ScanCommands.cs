using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using GsView = Autodesk.AutoCAD.GraphicsSystem.View;

[assembly: CommandClass(typeof(TextOutline.ScanCommands))]

namespace TextOutline
{
    // TXSCAN: window around text in a point cloud -> read it -> pick the font -> polylines.
    public class ScanCommands
    {
        static List<FontCandidate> fontCache;
        static bool keepText;

        [CommandMethod("TXSCAN", CommandFlags.Modal)]
        public void ScanCommand()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            try { Run(doc); }
            catch (System.Exception ex) { ed.WriteMessage("\nTXSCAN failed: {0}\n", ex.Message); }
        }

        static void Run(Document doc)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;

            if (System.Convert.ToInt32(AcApp.GetSystemVariable("PERSPECTIVE")) != 0)
            {
                ed.WriteMessage("\nTurn perspective off first (PERSPECTIVE = 0), then run TXSCAN again.\n");
                return;
            }

            ed.WriteMessage("\nZoom so the text fills a good part of the screen. Leave a little space around it.");
            PromptPointResult p1 = ed.GetPoint("\nFirst corner of a window around one line of text: ");
            if (p1.Status != PromptStatus.OK) return;
            PromptCornerOptions pco = new PromptCornerOptions("\nOther corner: ", p1.Value);
            PromptPointResult p2 = ed.GetCorner(pco);
            if (p2.Status != PromptStatus.OK) return;

            Matrix3d ucs = ed.CurrentUserCoordinateSystem;
            Point3d w1 = p1.Value.TransformBy(ucs), w2 = p2.Value.TransformBy(ucs);

            // 1. Capture the window as AutoCAD draws it.
            ViewCapture cap = ViewCapture.Take(doc, w1, w2);
            if (cap == null) return;
            ed.WriteMessage("\nCaptured {0} x {1} pixels. Reading the text...", cap.Image.Width, cap.Image.Height);

            // 2. Clean, straighten, read.
            ScanResult scan = ScanText.Analyze(cap.Image, true, null);
            if (scan.Level == null || scan.Level.W < 8 || scan.Level.H < 6)
            {
                ed.WriteMessage("\nNo letters found in that window. Zoom in closer, or change the point cloud colours " +
                                "so the letters stand out (intensity for painted text, elevation for engraved text).\n");
                SaveDebug(cap, scan, ed);
                return;
            }
            if (scan.Text.Length == 0)
                ed.WriteMessage("\nWindows OCR could not read it{0}. Type the text in the dialog.",
                                scan.OcrError.Length > 0 ? " (" + scan.OcrError + ")" : "");

            // 3. Let the user confirm the text and pick one of the best fonts.
            List<FontCandidate> fonts = Fonts(db);
            ScanDialog dlg = new ScanDialog(scan, fonts, keepText);
            if (AcApp.ShowModalDialog(dlg) != System.Windows.Forms.DialogResult.OK || dlg.Chosen == null) return;
            keepText = dlg.KeepText;
            FontMatch m = dlg.Chosen;
            string text = dlg.TextValue;

            // 4. Place real text in that font over the scan, then explode it to polylines.
            PointF[] c = scan.Corners();                 // bottom-left, bottom-right, top-right, top-left
            Point3d bl = cap.PixelToWorld(c[0]), br = cap.PixelToWorld(c[1]), tl = cap.PixelToWorld(c[3]);
            Vector3d normal = cap.ViewNormal;

            Report report = new Report();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                ObjectId styleId = Styles.FindOrCreate(db, tr, m.Font);
                DBText txt = Placement.Create(text, styleId, m, bl, br, tl, normal, space, tr);
                if (txt == null) { ed.WriteMessage("\nThe text could not be placed.\n"); return; }

                bool ok = Converter.Convert(txt, txt, space, tr, report);
                if (ok && !keepText && report.Polylines > 0) txt.Erase();
                tr.Commit();
            }

            ed.WriteMessage("\nTXSCAN: \"{0}\" in {1}, match {2:P0}. Created {3} polyline(s){4}.\n",
                            text, m.Font.Name, m.Score, report.Polylines, keepText ? " and kept the text" : "");
            if (report.Failed > 0) ed.WriteMessage("Could not convert: {0}\n", report.LastError);
        }

        // Installed TrueType fonts plus every SHX font AutoCAD can find (its Fonts folder and support paths).
        static List<FontCandidate> Fonts(Database db)
        {
            if (fontCache != null) return fontCache;
            List<FontCandidate> list = FontCandidate.InstalledTrueType();
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string txt = HostApplicationServices.Current.FindFile("txt.shx", db, FindFileHint.CompiledShapeFile);
                if (!string.IsNullOrEmpty(txt)) dirs.Add(Path.GetDirectoryName(txt));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            string prefix = AcApp.GetSystemVariable("ACADPREFIX") as string;
            if (!string.IsNullOrEmpty(prefix))
                foreach (string d in prefix.Split(';'))
                    if (d.Trim().Length > 0) dirs.Add(d.Trim().TrimEnd('\\'));

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d, "*.shx"))
                {
                    string name = Path.GetFileName(f);
                    if (seen.Contains(name)) continue;
                    try
                    {
                        ShxFont font = ShxFont.Load(f);
                        if (font.ShapeCount < 20) continue;       // shape libraries, not text fonts
                        seen.Add(name);
                        list.Add(FontCandidate.FromShx(font, name));
                    }
                    catch (System.Exception) { }              // big fonts and damaged files
                }
            }
            fontCache = list;
            return list;
        }

        // When nothing was found, keep the capture so the user (or an issue report) can see why.
        static void SaveDebug(ViewCapture cap, ScanResult scan, Editor ed)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "TextOutline");
                Directory.CreateDirectory(dir);
                string a = Path.Combine(dir, "txscan-capture.png"), b = Path.Combine(dir, "txscan-letters.png");
                cap.Image.Save(a, System.Drawing.Imaging.ImageFormat.Png);
                if (scan.Clean != null) scan.Clean.ToBitmap(0).Save(b, System.Drawing.Imaging.ImageFormat.Png);
                ed.WriteMessage("\nSaved what TXSCAN saw to {0}\n", dir);
            }
            catch (System.Exception) { }
        }
    }

    // A picture of a window of the current view, and the mapping from its pixels back to the drawing.
    sealed class ViewCapture
    {
        public Bitmap Image;
        public Vector3d ViewNormal;
        Matrix3d deviceToWorld;
        double rx, ry, z, flipH;
        bool yUp;

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        public static ViewCapture Take(Document doc, Point3d w1, Point3d w2)
        {
            Editor ed = doc.Editor;
            int vp = System.Convert.ToInt32(AcApp.GetSystemVariable("CVPORT"));
            GsView view = doc.GraphicsManager.GetCurrentAcGsView(vp);
            if (view == null)
            {
                ed.WriteMessage("\nCould not reach the current view. Click in the drawing area and try again.\n");
                return null;
            }

            ViewCapture cap = new ViewCapture();
            Matrix3d w2d = view.WorldToDeviceMatrix;
            cap.deviceToWorld = w2d.Inverse();
            Point3d d1 = w1.TransformBy(w2d), d2 = w2.TransformBy(w2d);
            cap.z = d1.Z;
            cap.ViewNormal = ed.GetCurrentView().ViewDirection.GetNormal();

            // Device y may run up or down: test with a point above the first corner.
            Point3d up = (w1 + (view.UpVector.GetNormal() * Math.Max(1e-6, w1.DistanceTo(w2) * 0.1))).TransformBy(w2d);
            cap.yUp = up.Y > d1.Y;
            if (cap.yUp)
            {
                RECT rc;
                if (!GetClientRect(doc.Window.Handle, out rc))
                {
                    ed.WriteMessage("\nCould not measure the drawing window.\n");
                    return null;
                }
                cap.flipH = rc.Bottom - rc.Top;
            }

            double x0 = Math.Min(d1.X, d2.X), x1 = Math.Max(d1.X, d2.X);
            double y0 = Math.Min(d1.Y, d2.Y), y1 = Math.Max(d1.Y, d2.Y);
            if (cap.yUp) { double t = y0; y0 = cap.flipH - y1; y1 = cap.flipH - t; }
            Rectangle rect = Rectangle.FromLTRB((int)Math.Ceiling(x0) + 2, (int)Math.Ceiling(y0) + 2,
                                                (int)Math.Floor(x1) - 2, (int)Math.Floor(y1) - 2);
            if (rect.Width < 16 || rect.Height < 8)
            {
                ed.WriteMessage("\nThe window is too small on screen: zoom in on the text and try again.\n");
                return null;
            }

            // Hide the grid while capturing, so its lines are not taken for letters.
            object grid = AcApp.GetSystemVariable("GRIDMODE");
            try
            {
                if (System.Convert.ToInt32(grid) != 0) AcApp.SetSystemVariable("GRIDMODE", 0);
                ed.UpdateScreen();
                view.Update();
                cap.Image = view.GetSnapshot(rect);
            }
            finally
            {
                if (System.Convert.ToInt32(grid) != 0) AcApp.SetSystemVariable("GRIDMODE", grid);
            }
            if (cap.Image == null)
            {
                ed.WriteMessage("\nAutoCAD returned no picture of the view.\n");
                return null;
            }
            cap.rx = rect.X;
            cap.ry = rect.Y;
            return cap;
        }

        public Point3d PixelToWorld(PointF p)
        {
            double dx = rx + p.X, dy = ry + p.Y;
            if (yUp) dy = flipH - dy;
            return new Point3d(dx, dy, z).TransformBy(deviceToWorld);
        }
    }

    // Text styles named after the font TXSCAN picked.
    static class Styles
    {
        public static ObjectId FindOrCreate(Database db, Transaction tr, FontCandidate f)
        {
            string name = "TXSCAN " + Clean(f.Name);
            TextStyleTable tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (tst.Has(name)) return tst[name];

            TextStyleTableRecord ts = new TextStyleTableRecord();
            ts.Name = name;
            ts.TextSize = 0;
            ts.XScale = 1;
            if (f.IsShx)
            {
                ts.FileName = f.ShxFile;
            }
            else
            {
                ts.Font = new FontDescriptor(f.Family.Name, (f.Style & FontStyle.Bold) != 0, (f.Style & FontStyle.Italic) != 0, 0, 0);
            }
            tst.UpgradeOpen();
            ObjectId id = tst.Add(ts);
            tr.AddNewlyCreatedDBObject(ts, true);
            return id;
        }

        static string Clean(string s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append("<>/\\\":;?*|,=`".IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    // Creates a TEXT that covers the scanned letters: same box, direction and plane.
    static class Placement
    {
        public static DBText Create(string text, ObjectId styleId, FontMatch m, Point3d bl, Point3d br, Point3d tl,
                                    Vector3d normal, BlockTableRecord space, Transaction tr)
        {
            Vector3d xdir = br - bl;
            double width = xdir.Length;
            if (width < 1e-9) return null;
            xdir = xdir.GetNormal();
            Vector3d ydir = normal.CrossProduct(xdir).GetNormal();
            double height = (tl - bl).DotProduct(ydir);
            if (height < 1e-9) return null;

            Vector3d ocsX = Vector3d.XAxis.TransformBy(Matrix3d.PlaneToWorld(normal));
            double rotation = ocsX.GetAngleTo(xdir, normal);

            DBText txt = new DBText();
            txt.SetDatabaseDefaults(space.Database);
            txt.TextString = text;
            txt.TextStyleId = styleId;
            txt.Normal = normal;
            txt.Position = bl;
            txt.Rotation = rotation;
            txt.Height = height;
            txt.WidthFactor = Math.Max(0.1, Math.Min(10, m.WidthFactor));
            txt.Oblique = m.ObliqueDeg * Math.PI / 180;
            space.AppendEntity(txt);
            tr.AddNewlyCreatedDBObject(txt, true);

            // Fit AutoCAD's own letter box onto the scanned one: height, then width, then position.
            Matrix3d toWorld = Matrix3d.Displacement(bl.GetAsVector())
                             * Matrix3d.PlaneToWorld(normal)
                             * Matrix3d.Rotation(rotation, Vector3d.ZAxis, Point3d.Origin);
            for (int pass = 0; pass < 3; pass++)
            {
                Extents3d e = Converter.LocalExtents(txt, toWorld, space, tr);
                double eh = e.MaxPoint.Y - e.MinPoint.Y, ew = e.MaxPoint.X - e.MinPoint.X;
                if (eh <= 0 || ew <= 0) break;
                txt.Height *= height / eh;
                e = Converter.LocalExtents(txt, toWorld, space, tr);
                ew = e.MaxPoint.X - e.MinPoint.X;
                if (ew > 0) txt.WidthFactor = Math.Max(0.1, Math.Min(10, txt.WidthFactor * (width / ew)));
                e = Converter.LocalExtents(txt, toWorld, space, tr);
                Vector3d shift = new Vector3d(-e.MinPoint.X, -e.MinPoint.Y, 0).TransformBy(toWorld);
                txt.Position = txt.Position + shift;
            }
            return txt;
        }
    }
}
