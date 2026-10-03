// Iki uygulamanin (OsiloTakip, OsiloKayit) ortak kullandigi dil, tema ve sayi bicimlendirme kodu.
using System;
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
