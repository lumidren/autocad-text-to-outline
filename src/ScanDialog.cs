using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TextOutline
{
    // TXSCAN's window: the scanned letters, the text Windows read (editable), the five best fonts
    // and a preview of the chosen font over the scan. No AutoCAD dependency.
    public sealed class ScanDialog : Form
    {
        readonly ScanResult scan;
        readonly IList<FontCandidate> fonts;
        readonly bool ocrKnewOrientation;
        List<FontMatch> matches = new List<FontMatch>();
        string matchedText = null;

        readonly PictureBox picScan = new PictureBox();
        readonly TextBox txtText = new TextBox();
        readonly Button btnMatch = new Button();
        readonly ListBox lstFonts = new ListBox();
        readonly PictureBox picPreview = new PictureBox();
        readonly Label lblInfo = new Label();
        readonly CheckBox chkKeep = new CheckBox();
        readonly Button btnOk = new Button();
        readonly Button btnCancel = new Button();

        public FontMatch Chosen { get; private set; }
        public string TextValue { get { return txtText.Text.Trim(); } }
        public bool KeepText { get { return chkKeep.Checked; } }

        public ScanDialog(ScanResult scan, IList<FontCandidate> fonts, bool keepText)
        {
            this.scan = scan;
            this.fonts = fonts;
            ocrKnewOrientation = scan.Text.Length > 0;

            Text = "TXSCAN - text from scan";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520);
            Font = new Font("Segoe UI", 9f);

            Label l1 = new Label();
            l1.Text = "Letters found in the scan (straightened):";
            l1.SetBounds(12, 10, 400, 18);
            picScan.SetBounds(12, 30, 736, 110);
            picScan.BackColor = Color.White;
            picScan.BorderStyle = BorderStyle.FixedSingle;
            picScan.SizeMode = PictureBoxSizeMode.Zoom;

            Label l2 = new Label();
            l2.Text = "Text (correct it if Windows read it wrong):";
            l2.SetBounds(12, 152, 400, 18);
            txtText.SetBounds(12, 172, 600, 24);
            txtText.Text = scan.Text;
            btnMatch.Text = "Match fonts";
            btnMatch.SetBounds(624, 171, 124, 27);
            btnMatch.Click += delegate { Match(); };

            Label l3 = new Label();
            l3.Text = "Best fonts:";
            l3.SetBounds(12, 210, 200, 18);
            lstFonts.SetBounds(12, 230, 300, 190);
            lstFonts.Font = new Font("Consolas", 10f);
            lstFonts.SelectedIndexChanged += delegate { ShowPreview(); };
            lstFonts.DoubleClick += delegate { Accept(); };

            Label l4 = new Label();
            l4.Text = "Preview: black = font and scan agree, grey = scan only, red = font only";
            l4.SetBounds(324, 210, 430, 18);
            picPreview.SetBounds(324, 230, 424, 190);
            picPreview.BackColor = Color.White;
            picPreview.BorderStyle = BorderStyle.FixedSingle;
            picPreview.SizeMode = PictureBoxSizeMode.Zoom;

            lblInfo.SetBounds(12, 428, 736, 36);
            lblInfo.ForeColor = Color.DimGray;
            chkKeep.Text = "Keep the text object as well";
            chkKeep.Checked = keepText;
            chkKeep.SetBounds(12, 478, 300, 24);

            btnOk.Text = "Draw polylines";
            btnOk.SetBounds(520, 476, 120, 30);
            btnOk.Click += delegate { Accept(); };
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.SetBounds(648, 476, 100, 30);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { l1, picScan, l2, txtText, btnMatch, l3, lstFonts, l4, picPreview,
                                              lblInfo, chkKeep, btnOk, btnCancel });
            ShowScan();
            lblInfo.Text = scan.Text.Length > 0
                ? "Windows read the text. Pick a font and click Draw polylines."
                : "Windows could not read the text" + (scan.OcrError.Length > 0 ? " (" + scan.OcrError + ")" : "") +
                  ". Type it above and click Match fonts.";
            Shown += delegate { if (TextValue.Length > 0) Match(); else txtText.Focus(); };
        }

        void ShowScan()
        {
            Image old = picScan.Image;
            picScan.Image = scan.Level.ToBitmap(8);
            if (old != null) old.Dispose();
        }

        void Match()
        {
            string text = TextValue;
            if (text.Length == 0)
            {
                lblInfo.Text = "Type the text first.";
                return;
            }
            Cursor = Cursors.WaitCursor;
            lblInfo.Text = "Comparing " + fonts.Count + " fonts...";
            Refresh();
            try
            {
                double before = scan.AngleDeg;
                matches = ocrKnewOrientation ? FontMatcher.Rank(scan.Level, text, fonts, 5)
                                             : FontMatcher.RankBothWays(scan, text, fonts, 5);
                if (scan.AngleDeg != before) ShowScan();
                matchedText = text;
                lstFonts.Items.Clear();
                for (int i = 0; i < matches.Count; i++)
                {
                    FontMatch m = matches[i];
                    string extra = (Math.Abs(m.WidthFactor - 1) > 0.05 ? string.Format("  w{0:F2}", m.WidthFactor) : "")
                                 + (m.ObliqueDeg != 0 ? "  slant" : "");
                    lstFonts.Items.Add(string.Format("{0}. {1,-24}{2,4:P0}{3}", i + 1, Short(m.Font.Name, 24), m.Score, extra));
                }
                if (matches.Count > 0) lstFonts.SelectedIndex = 0;
                lblInfo.Text = matches.Count == 0
                    ? "No font could draw that text."
                    : "Compared " + fonts.Count + " font styles. A score near 100% is a close match; check the preview.";
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        static string Short(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "~"; }

        void ShowPreview()
        {
            int i = lstFonts.SelectedIndex;
            if (i < 0 || i >= matches.Count) return;
            FontMatch m = matches[i];
            InkImage s = scan.Level.Crop(scan.Level.InkBounds());
            InkImage target = s.Resize(m.Render.W, m.Render.H);
            Bitmap b = new Bitmap(target.W + 8, target.H + 8, PixelFormat.Format24bppRgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            byte[] raw = new byte[d.Stride * b.Height];
            for (int k = 0; k < raw.Length; k++) raw[k] = 255;
            for (int y = 0; y < target.H; y++)
                for (int x = 0; x < target.W; x++)
                {
                    bool a = target[x, y], f = m.Render[x, y];
                    if (!a && !f) continue;
                    int o = ((y + 4) * d.Stride) + ((x + 4) * 3);
                    if (a && f) { raw[o] = raw[o + 1] = raw[o + 2] = 0; }
                    else if (a) { raw[o] = raw[o + 1] = raw[o + 2] = 190; }
                    else { raw[o] = 40; raw[o + 1] = 40; raw[o + 2] = 220; }
                }
            Marshal.Copy(raw, 0, d.Scan0, raw.Length);
            b.UnlockBits(d);
            Image old = picPreview.Image;
            picPreview.Image = b;
            if (old != null) old.Dispose();
        }

        void Accept()
        {
            if (TextValue.Length == 0) { lblInfo.Text = "Type the text first."; return; }
            if (matchedText != TextValue)
            {
                Match();                        // the text changed: show the new ranking before drawing
                return;
            }
            int i = lstFonts.SelectedIndex;
            if (i < 0 || i >= matches.Count) { lblInfo.Text = "Pick a font from the list."; return; }
            Chosen = matches[i];
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
