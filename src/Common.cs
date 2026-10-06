// Iki uygulamanin (ScopeRec, ScopeView) ortak kullandigi dil, tema ve sayi bicimlendirme kodu.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

// Dil ve tema: arayuz bunlar degisince bastan kurulur, o yuzden metinler ve renkler kurulum aninda okunur
static class Ui
{
    public static volatile bool En;
    public static string S(string tr, string en) { return En ? en : tr; }

    public static Theme Th = Theme.Light();
}

class Theme
{
    public bool Dark;
    public Color Back, Bar, Input, Text, Muted, Grid, Plot, Card, CardAlarm, Good, Bad, Border;
    public Color[] Chan;

    public static Theme Light()
    {
        Theme t = new Theme();
        t.Back = SystemColors.Control; t.Bar = Color.FromArgb(245, 246, 248); t.Input = SystemColors.Window;
        t.Text = SystemColors.ControlText; t.Muted = Color.DimGray; t.Grid = Color.Gainsboro; t.Plot = Color.White;
        t.Card = Color.White; t.CardAlarm = Color.FromArgb(255, 205, 210);
        t.Good = Color.FromArgb(46, 125, 50); t.Bad = Color.FromArgb(198, 40, 40); t.Border = Color.Silver;
        t.Chan = new Color[] { Color.FromArgb(200, 150, 0), Color.FromArgb(194, 24, 91), Color.FromArgb(0, 151, 167), Color.FromArgb(46, 125, 50) };
        return t;
    }

    public static Theme MakeDark()
    {
        Theme t = new Theme();
        t.Dark = true;
        t.Back = Color.FromArgb(30, 31, 34); t.Bar = Color.FromArgb(43, 45, 48); t.Input = Color.FromArgb(49, 51, 56);
        t.Text = Color.FromArgb(223, 225, 229); t.Muted = Color.FromArgb(150, 154, 160); t.Grid = Color.FromArgb(60, 63, 68); t.Plot = Color.FromArgb(24, 25, 28);
        t.Card = Color.FromArgb(43, 45, 48); t.CardAlarm = Color.FromArgb(110, 30, 34);
        t.Good = Color.FromArgb(102, 187, 106); t.Bad = Color.FromArgb(239, 83, 80); t.Border = Color.FromArgb(80, 84, 90);
        t.Chan = new Color[] { Color.FromArgb(255, 213, 0), Color.FromArgb(255, 105, 170), Color.FromArgb(64, 200, 220), Color.FromArgb(110, 210, 110) };
        return t;
    }
}

// Durum cubugunu tema rengiyle duz boyar
class FlatStripRenderer : ToolStripSystemRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(e.ToolStrip.BackColor)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
}

// Kenarligi ve basligi tema rengiyle cizen grup kutusu
class ThemedGroup : GroupBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme t = Ui.Th;
        e.Graphics.Clear(BackColor);
        Size ts = TextRenderer.MeasureText(Text, Font);
        using (Pen p = new Pen(t.Border)) e.Graphics.DrawRectangle(p, 0, ts.Height / 2, Width - 1, Height - ts.Height / 2 - 1);
        Rectangle r = new Rectangle(8, 0, ts.Width, ts.Height);
        using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, r);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, t.Text);
    }
}

// Sayi bicimlendirme: bilgisayarin bolge ayariyla, us gosterimi olmadan
static class Fmt
{
    static readonly CultureInfo Cur = CultureInfo.CurrentCulture;

    // 0.01166 V -> "11,66 mV"
    public static string Eng(double v, string unit)
    {
        if (double.IsNaN(v)) return "—";
        if (unit == "%") return v.ToString("0.##", Cur) + " %";
        if (unit.Length == 0) return Plain(v, 6); // birimsiz (seri port) degerler oldugu gibi
        double a = Math.Abs(v);
        string pre = ""; double k = 1;
        if (a == 0) { }
        else if (a >= 1e6) { pre = "M"; k = 1e-6; }
        else if (a >= 1e3) { pre = "k"; k = 1e-3; }
        else if (a >= 1) { }
        else if (a >= 1e-3) { pre = "m"; k = 1e3; }
        else if (a >= 1e-6) { pre = "µ"; k = 1e6; }
        else { pre = "n"; k = 1e9; }
        return Plain(v * k, 4) + " " + pre + unit;
    }

    // us gosterimi olmadan, 'digits' anlamli basamak
    public static string Plain(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return "";
        if (x == 0) return "0";
        int dec = digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(x)));
        decimal d = dec > 28 ? 0m : Math.Round((decimal)x, Math.Max(dec, 0));
        return d.ToString("0.############################", Cur);
    }

    // Kullanicinin yazdigi sayi: virgul ya da nokta ondalik ayirici olabilir; bos ya da gecersizse NaN
    public static double Parse(string s)
    {
        double v;
        s = s.Trim().Replace(',', '.');
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
    }

    public static string Span(double sec)
    {
        TimeSpan t = TimeSpan.FromSeconds(sec);
        return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
    }
}

// Grafikte farenin sag ustunde o andaki degeri gosteren kucuk kutu
static class HoverTip
{
    public static Label Create(Control host)
    {
        Label l = new Label();
        l.AutoSize = true; l.Visible = false; l.BorderStyle = BorderStyle.FixedSingle; l.Padding = new Padding(4, 3, 4, 3);
        l.BackColor = Ui.Th.Card; l.ForeColor = Ui.Th.Text;
        host.Controls.Add(l);
        l.BringToFront();
        return l;
    }

    // Kutuyu farenin sag ustune koyar; kenara tasarsa sola ya da alta alir (farenin altina girmesin diye hep aralikli)
    public static void Show(Label l, string text, Point mouse)
    {
        l.Text = text;
        Size sz = l.PreferredSize;
        int x = mouse.X + 14, y = mouse.Y - sz.Height - 10;
        if (x + sz.Width > l.Parent.Width - 2) x = mouse.X - sz.Width - 14;
        if (y < 2) y = mouse.Y + 18;
        l.Location = new Point(x, y);
        l.Visible = true;
    }
}

// Iki bolme arasindaki surukleme cubugu: tema rengiyle boyanir ve ortasinda tutamak noktalari olur ki yeri gorulsun
class GripSplitter : Splitter
{
    public GripSplitter(DockStyle dock)
    {
        Dock = dock;
        if (dock == DockStyle.Left || dock == DockStyle.Right) Width = 7; else Height = 7;
        BackColor = Ui.Th.Border;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        bool vertical = Width < Height;
        using (SolidBrush b = new SolidBrush(Ui.Th.Dark ? Color.FromArgb(200, 204, 210) : Color.FromArgb(90, 94, 100)))
            for (int i = -2; i <= 2; i++)
            {
                int x = vertical ? Width / 2 - 1 : Width / 2 - 1 + i * 8;
                int y = vertical ? Height / 2 - 1 + i * 8 : Height / 2 - 1;
                e.Graphics.FillRectangle(b, x, y, 3, 3);
            }
    }
}

// Uygulamanin kendi mesaj penceresi: temaya uyar (koyu temada koyu) ve Windows uyari sesi cikarmaz
static class Msg
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Evet / Hayir sorusu; varsayilan dugme Hayir
    public static bool Ask(IWin32Window owner, string text, string title) { return Show(owner, text, title, true) == DialogResult.Yes; }

    public static void Info(IWin32Window owner, string text, string title) { Show(owner, text, title, false); }

    static DialogResult Show(IWin32Window owner, string text, string title, bool yesNo)
    {
        Theme t = Ui.Th;
        using (Form f = new Form())
        {
            f.Text = title; f.Font = new Font("Segoe UI", 9.5f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.ShowIcon = false;
            f.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            f.BackColor = t.Back; f.ForeColor = t.Text;
            f.HandleCreated += delegate { int on = t.Dark ? 1 : 0; try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch (Exception) { } };

            Label l = new Label();
            l.AutoSize = true; l.MaximumSize = new Size(460, 0); l.Text = text; l.Location = new Point(22, 22);
            f.Controls.Add(l);
            Size ts = l.GetPreferredSize(new Size(460, 0));
            int w = Math.Max(yesNo ? 300 : 240, ts.Width + 44), y = ts.Height + 44;

            Panel bar = new Panel();
            bar.BackColor = t.Bar; bar.SetBounds(0, y, w, 56);
            f.Controls.Add(bar);
            List<Button> buttons = new List<Button>();
            if (yesNo)
            {
                buttons.Add(MakeButton(Ui.S("Evet", "Yes"), DialogResult.Yes));
                buttons.Add(MakeButton(Ui.S("Hayır", "No"), DialogResult.No));
            }
            else buttons.Add(MakeButton(Ui.S("Tamam", "OK"), DialogResult.OK));
            int x = w - 16;
            for (int i = buttons.Count - 1; i >= 0; i--) { x -= 96; buttons[i].SetBounds(x, 13, 90, 30); bar.Controls.Add(buttons[i]); x -= 6; }
            foreach (Button b in buttons)
                if (t.Dark) { b.FlatStyle = FlatStyle.Flat; b.BackColor = t.Input; b.ForeColor = t.Text; b.FlatAppearance.BorderColor = t.Border; }
            Button def = buttons[buttons.Count - 1]; // soruda Hayir, bilgide Tamam
            f.AcceptButton = def; f.CancelButton = def;
            f.ClientSize = new Size(w, y + 56);
            f.Shown += delegate { def.Focus(); };
            return owner != null ? f.ShowDialog(owner) : f.ShowDialog();
        }
    }

    static Button MakeButton(string text, DialogResult result)
    {
        Button b = new Button();
        b.Text = text; b.DialogResult = result;
        return b;
    }
}
