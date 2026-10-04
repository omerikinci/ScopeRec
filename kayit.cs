// ScopeView.exe - onceden alinmis olcum kayitlarini ve loglarini goruntuleme araci. Derleme icin: derle.bat
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

// Bir olcum dosyasindaki tek sutun
class Column
{
    public string Name, Unit;
    public double[] V;
    public double Min = double.MaxValue, Max = double.MinValue, Sum;
    public long N;
}

// Zaman cizgisinde gosterilen olay: logdan okunan, hesaplanan limit asimi ya da veri kesintisi
class Ev
{
    public double T;              // kayit basindan saniye
    public double Dur = double.NaN; // aralik olaylarinda sure
    public int Kind;              // 0 = bilgi, 1 = limit disi, 2 = veri kesintisi, 3 = hata
    public string Text = "", Value = "", Source = "";
}

class Recording
{
    public string Path;
    public double[] T = new double[0];
    public List<Column> Cols = new List<Column>();
    public DateTime Start;        // t = 0 anina karsilik gelen saat
    public bool HasClock;
    public double Offset;         // dosyadaki ilk t degeri; kayit suresi bundan sayilir (olay dosyalarindaki t ayni saatle yazilmis)

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Ayirici ve ondalik isareti basliktan anlasilir: ';' varsa ondalik virgul (Turkce Excel bicimi), yoksa ',' ve nokta
    public static char DetectSep(string header) { return header.IndexOf(';') >= 0 ? ';' : header.IndexOf('\t') >= 0 ? '\t' : ','; }

    public static double Num(string s, char sep)
    {
        double v;
        if (sep != ',') s = s.Replace(',', '.');
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN;
    }

    public static bool Clock(string s, out DateTime d)
    {
        return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)
            || DateTime.TryParse(s, Inv, DateTimeStyles.None, out d)
            || DateTime.TryParse(s, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out d);
    }

    public static bool LooksLikeMeasurement(string header)
    {
        string[] h = header.Split(DetectSep(header));
        return h.Length >= 3 && h[1].Trim().StartsWith("t") && h[2].IndexOf('_') > 0;
    }

    public static Recording Load(string path)
    {
        Recording r = new Recording();
        r.Path = path;
        List<double> t = new List<double>();
        List<List<double>> cols = new List<List<double>>();
        using (StreamReader sr = new StreamReader(path, Encoding.UTF8))
        {
            string header = sr.ReadLine();
            if (header == null) throw new InvalidDataException(Ui.S("Dosya boş", "File is empty"));
            char sep = DetectSep(header);
            string[] h = header.Split(sep);
            if (!LooksLikeMeasurement(header)) throw new InvalidDataException(Ui.S("Ölçüm dosyası biçiminde değil", "Not a measurement file"));
            for (int i = 2; i < h.Length; i++)
            {
                Column c = new Column();
                Match m = Regex.Match(h[i].Trim(), @"^(.*?)(\[(.*)\])?$");
                c.Name = m.Groups[1].Value.Replace('_', ' ');
                c.Unit = m.Groups[3].Success ? m.Groups[3].Value : GuessUnit(c.Name);
                r.Cols.Add(c);
                cols.Add(new List<double>());
            }
            string line;
            bool first = true;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split(sep);
                if (f.Length < 2) continue;
                double tv = Num(f[1], sep);
                if (double.IsNaN(tv)) continue;
                if (first)
                {
                    DateTime d;
                    // saat sutunu saniyeye yuvarlanmis yaziliyor; ortalama yarim saniyelik kaymayi duzelt
                    if (Clock(f[0], out d)) { r.Start = d.AddSeconds(0.5); r.HasClock = true; }
                    r.Offset = tv;
                    first = false;
                }
                t.Add(tv - r.Offset); // kayit suresi kaydin ilk satirindan baslar
                for (int i = 0; i < cols.Count; i++) cols[i].Add(i + 2 < f.Length ? Num(f[i + 2], sep) : double.NaN);
            }
        }
        if (!r.HasClock) r.Start = DateTime.Today;
        r.T = t.ToArray();
        for (int i = 0; i < cols.Count; i++)
        {
            Column c = r.Cols[i];
            c.V = cols[i].ToArray();
            foreach (double v in c.V)
            {
                if (double.IsNaN(v)) continue;
                if (v < c.Min) c.Min = v;
                if (v > c.Max) c.Max = v;
                c.Sum += v; c.N++;
            }
        }
        return r;
    }

    // Eski kayitlarda basliklarda birim yok; parametre adindan cikarilir
    static string GuessUnit(string name)
    {
        string p = name.Substring(name.LastIndexOf(' ') + 1).ToUpperInvariant();
        if (p == "FREQ") return "Hz";
        if (p == "PER" || p == "PWID" || p == "NWID" || p == "RISE" || p == "FALL") return "s";
        if (p == "DUTY") return "%";
        return "V";
    }

    // Ornekler arasi sure normalin cok ustune cikan yerler: kayit o arada veri alamamis
    public List<Ev> FindGaps()
    {
        List<Ev> list = new List<Ev>();
        if (T.Length < 10) return list;
        List<double> d = new List<double>();
        for (int i = 1; i < Math.Min(T.Length, 2000); i++) d.Add(T[i] - T[i - 1]);
        d.Sort();
        double limit = Math.Max(d[d.Count / 2] * 5, 2.0);
        for (int i = 1; i < T.Length; i++)
            if (T[i] - T[i - 1] > limit)
            {
                Ev e = new Ev();
                e.T = T[i - 1]; e.Dur = T[i] - T[i - 1]; e.Kind = 2;
                e.Text = Ui.S("Veri kesintisi", "Data gap"); e.Source = Ui.S("hesap", "computed");
                list.Add(e);
            }
        return list;
    }

    // Bir sutunun alt/ust limitin disinda kaldigi araliklar (gecersiz olcum de limit disi sayilir)
    public List<Ev> FindExcursions(Column c, double low, double high)
    {
        List<Ev> list = new List<Ev>();
        Ev open = null;
        double worst = 0;
        for (int i = 0; i < T.Length; i++)
        {
            double v = c.V[i];
            bool bad = double.IsNaN(v) || v < low || v > high;
            if (bad && open == null)
            {
                open = new Ev();
                open.T = T[i]; open.Kind = 1; open.Source = Ui.S("hesap", "computed");
                open.Text = Ui.S("Limit dışı  ", "Out of limit  ") + c.Name;
                worst = v;
            }
            if (bad && open != null && !double.IsNaN(v) && (double.IsNaN(worst) || Math.Abs(v - Mid(low, high, v)) > Math.Abs(worst - Mid(low, high, worst)))) worst = v;
            if ((!bad || i == T.Length - 1) && open != null)
            {
                open.Dur = T[i] - open.T;
                open.Value = Fmt.Eng(worst, c.Unit);
                list.Add(open);
                open = null;
            }
        }
        return list;
    }

    static double Mid(double low, double high, double v)
    {
        if (!double.IsNaN(low) && !double.IsNaN(high)) return (low + high) / 2;
        return double.IsNaN(low) ? high : low;
    }
}

static class EventFile
{
    // _log.txt (zaman <sekme> t <sekme> mesaj) ya da _olaylar.csv (zaman;t;olay;deger;sure) okur
    public static List<Ev> Load(string path)
    {
        List<Ev> list = new List<Ev>();
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return list;
        char sep = Recording.DetectSep(lines[0]);
        string src = System.IO.Path.GetFileName(path).EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "log" : Ui.S("olay dosyası", "event file");
        Ev open = null;
        foreach (string line in lines)
        {
            string[] f = line.Split(sep);
            if (f.Length < 3) continue;
            double t = Recording.Num(f[1].Trim(), sep == '\t' ? ';' : sep);
            if (double.IsNaN(t)) continue; // baslik satiri
            string msg = f[2].Trim();
            string low = msg.ToLowerInvariant();
            bool start = low.StartsWith("limit disi") || low.StartsWith("li̇mi̇t dişi") || low.StartsWith("limit dışı") || msg.StartsWith("LİMİT DIŞI") || low.StartsWith("out of limit");
            bool end = low.StartsWith("normale d") || low.StartsWith("back to normal");
            if (end)
            {
                if (open != null) { open.Dur = t - open.T; open = null; }
                continue;
            }
            Ev e = new Ev();
            e.T = t; e.Text = msg; e.Source = src;
            if (f.Length > 3) e.Value = f[3].Trim();
            if (start) { e.Kind = 1; open = e; }
            else if (low.Contains("hata") || low.Contains("error") || low.Contains("koptu") || low.Contains("lost") || low.Contains("bulunamad") || low.Contains("not found")) e.Kind = 3;
            list.Add(e);
        }
        return list;
    }
}

class ViewerForm : Form
{
    static readonly CultureInfo Cur = CultureInfo.CurrentCulture;
    const int MaxBuckets = 2000;

    Recording rec;
    List<Ev> fileEvents = new List<Ev>(), gapEvents = new List<Ev>(), calcEvents = new List<Ev>(), shown = new List<Ev>();
    double viewStart, viewEnd; // saniye
    double yLo = double.NaN, yHi = double.NaN; // Ctrl+tekerlekle secilen sol eksen araligi; NaN = otomatik
    bool updating;

    Chart chart;
    HScrollBar scroll;
    CheckedListBox lstCols;
    ListView lvEvents, lvStats;
    ComboBox cmbLimit, cmbAxis;
    TextBox txtLow, txtHigh;
    Label lblHint, lblFiles, lblSummary, lblLow, lblHigh, lblHover;
    Point hoverPt;
    Panel topBar;
    StatusStrip status;
    ToolStripStatusLabel stCursor, stView;
    readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    // ScopeRec'te secilen kayit yeri; "Dosya ac" penceresi burada acilir
    string recBase = AppDomain.CurrentDomain.BaseDirectory, recName = "kayitlar";
    string recDir { get { try { return Path.Combine(recBase, recName); } catch (ArgumentException) { return baseDir; } } }

    public ViewerForm()
    {
        LoadPrefs();
        Text = Ui.S("ScopeView – kayıt görüntüleyici", "ScopeView – recording viewer");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += delegate(object s, DragEventArgs e) { OpenFiles((string[])e.Data.GetData(DataFormats.FileDrop)); };
        BuildUi();
        ApplyTheme();
        ShowData();
    }

    // Dil ve tema ScopeRec'in ayar dosyasindan alinir
    void LoadPrefs()
    {
        Ui.En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr";
        string ini = Path.Combine(baseDir, "ayarlar.ini");
        if (!File.Exists(ini)) return;
        foreach (string line in File.ReadAllLines(ini))
        {
            if (line == "dil=en") Ui.En = true;
            else if (line == "dil=tr") Ui.En = false;
            else if (line == "tema=koyu") Ui.Th = Theme.MakeDark();
            else if (line.StartsWith("kayit_yeri=") && line.Length > 11) recBase = line.Substring(11);
            else if (line.StartsWith("kayit_klasoru=") && line.Length > 14) recName = line.Substring(14);
        }
    }

    // ---------------------------------------------------------------- arayuz

    void BuildUi()
    {
        TableLayoutPanel main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill; main.ColumnCount = 1; main.RowCount = 3; main.Padding = new Padding(4);
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));

        Panel chartHost = new Panel();
        chartHost.Dock = DockStyle.Fill;
        chart = new Chart();
        chart.Dock = DockStyle.Fill;
        ChartArea a = new ChartArea("a");
        // X ekseni saniye cinsinden kayit suresi; etiketler FormatNumber olayinda sure ya da saat olarak yazilir
        a.AxisX.ScaleView.Zoomable = false;         // yakinlastirmayi kendimiz yonetiyoruz (gorunen aralik yeniden orneklenir)
        a.CursorX.IsUserEnabled = true; a.CursorX.IsUserSelectionEnabled = true;
        a.CursorX.Interval = 0;
        a.AxisY.IsStartedFromZero = false; a.AxisY2.IsStartedFromZero = false;
        a.AxisY.LabelStyle.Format = "0.###"; a.AxisY2.LabelStyle.Format = "0.###";
        a.AxisY2.MajorGrid.Enabled = false;
        chart.ChartAreas.Add(a);
        Legend lg = new Legend("l"); lg.Docking = Docking.Top;
        chart.Legends.Add(lg);
        chart.FormatNumber += delegate(object s, FormatNumberEventArgs e)
        {
            if (rec != null && e.ElementType == ChartElementType.AxisLabels && s == chart.ChartAreas[0].AxisX) e.LocalizedValue = AxisLabel(e.Value);
        };
        chart.SelectionRangeChanged += delegate(object s, CursorEventArgs e)
        {
            if (rec == null || double.IsNaN(e.NewSelectionStart) || double.IsNaN(e.NewSelectionEnd)) return;
            double x0 = ToSec(Math.Min(e.NewSelectionStart, e.NewSelectionEnd)), x1 = ToSec(Math.Max(e.NewSelectionStart, e.NewSelectionEnd));
            chart.ChartAreas[0].CursorX.SetSelectionPosition(double.NaN, double.NaN);
            if (x1 - x0 > 0.05) SetView(x0, x1);
        };
        chart.MouseWheel += delegate(object s, MouseEventArgs e)
        {
            if (rec == null) return;
            double c = CursorSeconds(e.X);
            if (double.IsNaN(c)) c = (viewStart + viewEnd) / 2;
            double k = e.Delta > 0 ? 0.6 : 1 / 0.6;
            if ((ModifierKeys & Keys.Control) != 0)
            {
                // Ctrl + tekerlek: deger ekseninde (sol eksen) yakinlastir
                Axis ay = chart.ChartAreas[0].AxisY;
                try
                {
                    double lo = ay.Minimum, hi = ay.Maximum;
                    if (double.IsNaN(lo) || double.IsNaN(hi) || hi <= lo) return;
                    double cy = Math.Max(lo, Math.Min(hi, ay.PixelPositionToValue(e.Y)));
                    yLo = cy - (cy - lo) * k; yHi = cy + (hi - cy) * k;
                }
                catch (Exception) { return; }
                Redraw();
                return;
            }
            SetView(c - (c - viewStart) * k, c + (viewEnd - c) * k);
        };
        chart.MouseEnter += delegate { if (rec != null) chart.Focus(); };
        chart.MouseMove += delegate(object s, MouseEventArgs e) { hoverPt = e.Location; ShowCursor(e.X); };
        chart.MouseLeave += delegate { lblHover.Visible = false; };
        chart.MouseDoubleClick += delegate { ShowAll(); };
        lblHint = new Label();
        lblHint.Dock = DockStyle.Fill; lblHint.TextAlign = ContentAlignment.MiddleCenter;
        lblHint.Font = new Font("Segoe UI", 14f);
        lblHint.Text = Ui.S("Ölçüm kaydını (olcum_….csv) ve log dosyasını (…_log.txt)\nbu pencereye sürükleyip bırakın",
                            "Drag and drop the measurement file (olcum_….csv)\nand the log file (…_log.txt) onto this window");
        lblHover = HoverTip.Create(chart);
        chartHost.Controls.Add(chart);
        chartHost.Controls.Add(lblHint);
        main.Controls.Add(chartHost, 0, 0);

        scroll = new HScrollBar();
        scroll.Dock = DockStyle.Fill; scroll.Enabled = false;
        scroll.ValueChanged += delegate { if (!updating && rec != null) { double w = viewEnd - viewStart; SetView(T0 + scroll.Value / 10.0, T0 + scroll.Value / 10.0 + w); } };
        main.Controls.Add(scroll, 0, 1);

        TableLayoutPanel bottom = new TableLayoutPanel();
        bottom.Dock = DockStyle.Fill; bottom.ColumnCount = 2; bottom.RowCount = 1;
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        lvEvents = MakeList(new string[] { Ui.S("Kayıt süresi", "Elapsed"), Ui.S("Saat", "Clock"), Ui.S("Olay", "Event"), Ui.S("Değer", "Value"), Ui.S("Olay süresi", "Duration"), Ui.S("Kaynak", "Source") },
                            new int[] { 82, 125, 200, 76, 76, 84 });
        lvEvents.SelectedIndexChanged += delegate { if (lvEvents.SelectedIndices.Count > 0) GoTo(shown[lvEvents.SelectedIndices[0]]); };
        lvStats = MakeList(new string[] { Ui.S("Seri", "Series"), "Min", Ui.S("Maks", "Max"), Ui.S("Ortalama", "Mean") }, new int[] { 110, 90, 90, 90 });
        bottom.Controls.Add(lvEvents, 0, 0);
        bottom.Controls.Add(lvStats, 1, 0);
        main.Controls.Add(bottom, 0, 2);

        // sol panel
        Panel left = new Panel();
        left.Dock = DockStyle.Left; left.Width = 250; left.Padding = new Padding(6);
        GroupBox gCols = new ThemedGroup();
        gCols.Text = Ui.S("Gösterilecek seriler", "Series to show"); gCols.SetBounds(6, 6, 234, 190);
        lstCols = new CheckedListBox();
        lstCols.CheckOnClick = true; lstCols.IntegralHeight = false; lstCols.SetBounds(8, 22, 218, 160);
        lstCols.ItemCheck += delegate { yLo = yHi = double.NaN; BeginInvoke(new MethodInvoker(Redraw)); };
        gCols.Controls.Add(lstCols);

        GroupBox gLim = new ThemedGroup();
        gLim.Text = Ui.S("Limit analizi", "Limit analysis"); gLim.SetBounds(6, 204, 234, 250);
        cmbLimit = new ComboBox(); cmbLimit.DropDownStyle = ComboBoxStyle.DropDownList; cmbLimit.SetBounds(12, 24, 214, 24);
        cmbLimit.SelectedIndexChanged += delegate { UpdateLimitLabels(); };
        lblLow = new Label(); lblLow.SetBounds(12, 60, 100, 20);
        txtLow = new TextBox(); txtLow.SetBounds(116, 57, 110, 24);
        lblHigh = new Label(); lblHigh.SetBounds(12, 90, 100, 20);
        txtHigh = new TextBox(); txtHigh.SetBounds(116, 87, 110, 24);
        Button btnCalc = new Button();
        btnCalc.Text = Ui.S("Limit dışı yerleri bul", "Find out-of-limit spans"); btnCalc.SetBounds(12, 120, 214, 30);
        btnCalc.Click += delegate { Analyze(); };
        lblSummary = new Label(); lblSummary.SetBounds(12, 158, 214, 86);
        gLim.Controls.AddRange(new Control[] { cmbLimit, lblLow, txtLow, lblHigh, txtHigh, btnCalc, lblSummary });
        left.Controls.Add(gCols); left.Controls.Add(gLim);

        // ust cubuk
        topBar = new Panel();
        topBar.Dock = DockStyle.Top; topBar.Height = 50;
        Button btnOpen = new Button();
        btnOpen.Text = Ui.S("Dosya aç…", "Open files…"); btnOpen.SetBounds(10, 10, 110, 30);
        btnOpen.Click += delegate
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Filter = Ui.S("Kayıt ve log dosyaları", "Recording and log files") + "|*.csv;*.txt";
                string dir = recDir;
                if (Directory.Exists(dir)) d.InitialDirectory = dir;
                if (d.ShowDialog(this) == DialogResult.OK) OpenFiles(d.FileNames);
            }
        };
        Button btnAll = new Button();
        btnAll.Text = Ui.S("Tümünü göster", "Show all"); btnAll.SetBounds(126, 10, 110, 30);
        btnAll.Click += delegate { ShowAll(); };
        lblFiles = new Label();
        lblFiles.SetBounds(470, 6, 700, 40); lblFiles.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        lblFiles.Text = Ui.S("Tekerlek: zamanda yakınlaştır  ·  Ctrl+tekerlek: değerde yakınlaştır  ·  sürükle: aralık seç\nçift tık: tümünü göster  ·  olaya tıkla: oraya git",
                             "Wheel: zoom time  ·  Ctrl+wheel: zoom values  ·  drag: select range\ndouble click: show all  ·  click an event: go there");
        Label lAxis = new Label();
        lAxis.Text = Ui.S("Zaman ekseni:", "Time axis:"); lAxis.SetBounds(248, 17, 84, 20);
        cmbAxis = new ComboBox(); cmbAxis.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbAxis.Items.AddRange(new object[] { Ui.S("Kayıt süresi", "Elapsed time"), Ui.S("Saat", "Clock time") });
        cmbAxis.SelectedIndex = 0; cmbAxis.SetBounds(334, 13, 120, 24);
        cmbAxis.SelectedIndexChanged += delegate { Redraw(); };
        topBar.Controls.AddRange(new Control[] { btnOpen, btnAll, lAxis, cmbAxis, lblFiles });

        status = new StatusStrip();
        stCursor = new ToolStripStatusLabel(""); stCursor.Spring = true; stCursor.TextAlign = ContentAlignment.MiddleLeft;
        stView = new ToolStripStatusLabel("");
        status.Items.AddRange(new ToolStripItem[] { stCursor, stView });

        Controls.Add(main);
        Controls.Add(left);
        Controls.Add(topBar);
        Controls.Add(status);
    }

    ListView MakeList(string[] heads, int[] widths)
    {
        ListView lv = new ListView();
        lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.HideSelection = false; lv.MultiSelect = false;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        for (int i = 0; i < heads.Length; i++) lv.Columns.Add(heads[i], widths[i]);
        return lv;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = Ui.Th.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }
    }

    void ApplyTheme()
    {
        Theme t = Ui.Th;
        BackColor = t.Back; ForeColor = t.Text;
        topBar.BackColor = t.Bar;
        lblHint.ForeColor = t.Muted;
        PaintControls(this);
        status.BackColor = t.Bar; status.SizingGrip = false; status.Renderer = new FlatStripRenderer();
        foreach (ToolStripItem it in status.Items) it.ForeColor = t.Text;
        chart.BackColor = t.Back;
        ChartArea a = chart.ChartAreas[0];
        a.BackColor = t.Plot;
        a.CursorX.SelectionColor = Color.FromArgb(70, t.Chan[2]);
        a.CursorX.LineColor = t.Muted;
        foreach (Axis ax in new Axis[] { a.AxisX, a.AxisY, a.AxisY2 })
        {
            ax.LineColor = t.Border; ax.MajorTickMark.LineColor = t.Border; ax.MajorGrid.LineColor = t.Grid;
            ax.LabelStyle.ForeColor = t.Text; ax.TitleForeColor = t.Text;
        }
        chart.Legends[0].BackColor = Color.Transparent; chart.Legends[0].ForeColor = t.Text;
        if (t.Dark) try { SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
    }

    void PaintControls(Control parent)
    {
        Theme t = Ui.Th;
        foreach (Control c in parent.Controls)
        {
            if (c is TextBox || c is ListBox || c is ComboBox || c is ListView)
            {
                c.BackColor = t.Input; c.ForeColor = t.Text;
                if (t.Dark)
                {
                    if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                    if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                    if (c is ListBox) ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
                    if (c is ListBox || c is ListView) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
                }
            }
            else if (c is Button && t.Dark)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat; b.BackColor = t.Input; b.ForeColor = t.Text; b.FlatAppearance.BorderColor = t.Border;
            }
            PaintControls(c);
        }
    }

    // ---------------------------------------------------------------- dosya acma

    // Birakilan dosyalari turune gore ayirir; olcum dosyasi tek basina birakilirsa yanindaki log/olay dosyasi da acilir
    public void OpenFiles(string[] paths)
    {
        string meas = null;
        List<string> logs = new List<string>();
        List<string> problems = new List<string>();
        foreach (string p in paths)
        {
            try
            {
                string head;
                using (StreamReader sr = new StreamReader(p, Encoding.UTF8)) head = sr.ReadLine() ?? "";
                if (Recording.LooksLikeMeasurement(head)) meas = p; else logs.Add(p);
            }
            catch (Exception e) { problems.Add(Path.GetFileName(p) + ": " + e.Message); }
        }
        try
        {
            if (meas != null)
            {
                Cursor = Cursors.WaitCursor;
                rec = Recording.Load(meas);
                fileEvents.Clear(); calcEvents.Clear();
                gapEvents = rec.FindGaps();
                if (logs.Count == 0)
                {
                    string stem = Path.Combine(Path.GetDirectoryName(meas), Path.GetFileNameWithoutExtension(meas));
                    // log daha ayrintili (baglanti, hata, limit); yoksa olay dosyasina bakilir
                    if (File.Exists(stem + "_log.txt")) logs.Add(stem + "_log.txt");
                    else if (File.Exists(stem + "_olaylar.csv")) logs.Add(stem + "_olaylar.csv");
                }
            }
            foreach (string l in logs)
            {
                if (rec == null) { problems.Add(Ui.S("Önce ölçüm dosyasını (olcum_….csv) bırakın", "Drop the measurement file (olcum_….csv) first")); break; }
                foreach (Ev e in EventFile.Load(l)) { e.T -= rec.Offset; fileEvents.Add(e); } // olay zamanlarini kayit suresine cevir
            }
            // Kayit bittiginde hala limit disinda kalan olay: sonuna kadar surmus say
            if (rec != null)
                foreach (Ev e in fileEvents)
                    if (e.Kind == 1 && double.IsNaN(e.Dur)) e.Dur = Math.Max(0, T1 - e.T);
        }
        catch (Exception e) { problems.Add(e.Message); }
        finally { Cursor = Cursors.Default; }
        ShowData();
        if (problems.Count > 0) MessageBox.Show(this, string.Join("\n", problems.ToArray()), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (rec != null)
        {
            string names = Path.GetFileName(rec.Path);
            foreach (string l in logs) names += "   +   " + Path.GetFileName(l);
            Text = "ScopeView – " + names;
        }
    }

    double T0 { get { return rec.T.Length > 0 ? rec.T[0] : 0; } }
    double T1 { get { return rec.T.Length > 0 ? rec.T[rec.T.Length - 1] : 1; } }
    double ToX(double sec) { return sec; }
    double ToSec(double x) { return x; }

    void ShowData()
    {
        bool has = rec != null && rec.T.Length > 0;
        lblHint.Visible = !has; chart.Visible = has;
        lstCols.Items.Clear(); cmbLimit.Items.Clear(); lvStats.Items.Clear();
        if (!has) { RefreshEvents(); return; }
        updating = true;
        foreach (Column c in rec.Cols)
        {
            // ilk serinin birimindeki seriler acilista isaretli gelir
            lstCols.Items.Add(c.Name + " [" + c.Unit + "]", c.Unit == rec.Cols[0].Unit);
            cmbLimit.Items.Add(c.Name);
            ListViewItem it = new ListViewItem(c.Name);
            it.SubItems.Add(c.N > 0 ? Fmt.Eng(c.Min, c.Unit) : "—");
            it.SubItems.Add(c.N > 0 ? Fmt.Eng(c.Max, c.Unit) : "—");
            it.SubItems.Add(c.N > 0 ? Fmt.Eng(c.Sum / c.N, c.Unit) : "—");
            lvStats.Items.Add(it);
        }
        cmbLimit.SelectedIndex = 0;
        updating = false;
        lblSummary.Text = "";
        RefreshEvents();
        ShowAll();
    }

    void UpdateLimitLabels()
    {
        if (rec == null || cmbLimit.SelectedIndex < 0) return;
        string u = rec.Cols[cmbLimit.SelectedIndex].Unit;
        lblLow.Text = Ui.S("Alt limit [", "Lower limit [") + u + "]:";
        lblHigh.Text = Ui.S("Üst limit [", "Upper limit [") + u + "]:";
    }

    void Analyze()
    {
        if (rec == null || cmbLimit.SelectedIndex < 0) return;
        double lo = Fmt.Parse(txtLow.Text), hi = Fmt.Parse(txtHigh.Text);
        if (double.IsNaN(lo) && double.IsNaN(hi))
        {
            MessageBox.Show(this, Ui.S("En az bir limit girin.", "Enter at least one limit."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Column c = rec.Cols[cmbLimit.SelectedIndex];
        calcEvents = rec.FindExcursions(c, lo, hi);
        double total = 0, longest = 0;
        foreach (Ev e in calcEvents) { total += e.Dur; if (e.Dur > longest) longest = e.Dur; }
        double span = Math.Max(T1 - T0, 1e-9);
        lblSummary.Text = calcEvents.Count == 0
            ? Ui.S("Limit dışına çıkılmamış.", "Never out of limits.")
            : string.Format(Cur, Ui.S("{0} kez limit dışı\ntoplam {1} (%{2:0.##})\nen uzun {3}", "{0} out-of-limit spans\ntotal {1} ({2:0.##} %)\nlongest {3}"),
                calcEvents.Count, Dur(total), total / span * 100, Dur(longest));
        RefreshEvents();
        Redraw();
    }

    bool ClockAxis { get { return cmbAxis.SelectedIndex == 1 && rec != null && rec.HasClock; } }
    double axisStep = 1;

    static double NiceStep(double raw)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };
        foreach (double s in steps) if (s >= raw) return s;
        return Math.Ceiling(raw / 86400) * 86400;
    }

    // Eksen etiketi: kayit suresi (saat:dakika:saniye, saat 24'u asabilir) ya da gercek saat
    string AxisLabel(double sec)
    {
        if (ClockAxis)
        {
            DateTime d = rec.Start.AddSeconds(sec);
            return d.ToString(axisStep < 1 ? "HH:mm:ss.f" : T1 - T0 > 86400 ? "dd.MM HH:mm" : "HH:mm:ss");
        }
        if (axisStep < 1) { TimeSpan t = TimeSpan.FromSeconds(Math.Round(sec, 1)); return string.Format(Cur, "{0:00}:{1:00}:{2:00.0}", (int)t.TotalHours, t.Minutes, t.Seconds + t.Milliseconds / 1000.0); }
        return Fmt.Span(Math.Round(sec));
    }

    static string Dur(double sec)
    {
        if (double.IsNaN(sec)) return "";
        if (sec < 60) return sec.ToString("0.##", Cur) + " s";
        return Fmt.Span(sec);
    }

    string ClockText(double sec)
    {
        return rec.HasClock ? rec.Start.AddSeconds(sec).ToString("dd.MM.yyyy HH:mm:ss") : "";
    }

    void RefreshEvents()
    {
        shown = new List<Ev>();
        shown.AddRange(fileEvents); shown.AddRange(calcEvents); shown.AddRange(gapEvents);
        shown.Sort(delegate(Ev a, Ev b) { return a.T.CompareTo(b.T); });
        lvEvents.BeginUpdate();
        lvEvents.Items.Clear();
        foreach (Ev e in shown)
        {
            ListViewItem it = new ListViewItem(Fmt.Span(e.T));
            it.SubItems.Add(rec != null ? ClockText(e.T) : "");
            it.SubItems.Add(e.Text);
            it.SubItems.Add(e.Value);
            it.SubItems.Add(Dur(e.Dur));
            it.SubItems.Add(e.Source);
            if (e.Kind != 0) it.ForeColor = KindColor(e.Kind);
            lvEvents.Items.Add(it);
        }
        lvEvents.EndUpdate();
    }

    static Color KindColor(int kind)
    {
        if (kind == 1) return Ui.Th.Bad;
        if (kind == 2) return Ui.Th.Muted;
        if (kind == 3) return Color.FromArgb(230, 140, 0);
        return Ui.Th.Chan[2];
    }

    // ---------------------------------------------------------------- gorunum

    void ShowAll() { yLo = yHi = double.NaN; if (rec != null) SetView(T0, T1); }

    // Olaya git: olayin cevresini, suresinin birkac kati genislikte goster
    void GoTo(Ev e)
    {
        double d = double.IsNaN(e.Dur) ? 0 : e.Dur;
        double pad = Math.Max(d * 2, 10);
        SetView(e.T - pad, e.T + d + pad);
    }

    void SetView(double a, double b)
    {
        if (rec == null) return;
        double full = Math.Max(T1 - T0, 0.1), w = Math.Min(Math.Max(b - a, 0.1), full);
        if (a < T0) a = T0;
        if (a + w > T0 + full) a = T0 + full - w;
        viewStart = a; viewEnd = a + w;
        updating = true;
        bool zoomed = w < full - 1e-9;
        scroll.Enabled = zoomed;
        if (zoomed)
        {
            int max = (int)Math.Ceiling(full * 10), large = (int)Math.Max(1, w * 10);
            int v = (int)Math.Max(0, Math.Min((viewStart - T0) * 10, max - large + 1));
            scroll.Value = 0; scroll.Maximum = max; scroll.LargeChange = large; scroll.SmallChange = Math.Max(1, large / 10);
            scroll.Value = v;
        }
        updating = false;
        Redraw();
    }

    void Redraw()
    {
        if (rec == null || updating) return;
        ChartArea area = chart.ChartAreas[0];
        int i0 = Array.BinarySearch(rec.T, viewStart), i1 = Array.BinarySearch(rec.T, viewEnd);
        if (i0 < 0) i0 = ~i0;
        if (i1 < 0) i1 = ~i1; else i1++;
        i0 = Math.Max(0, i0 - 1); i1 = Math.Min(rec.T.Length, i1 + 1); // kenarlarda cizgi kopmasin

        // Ilk isaretli serinin birimi sol eksene, farkli ilk birim sag eksene; ucuncu bir birim cizilmez
        string unit1 = null, unit2 = null;
        chart.Series.Clear();
        List<string> skipped = new List<string>();
        for (int ci = 0; ci < rec.Cols.Count; ci++)
        {
            if (!lstCols.GetItemChecked(ci)) continue;
            Column c = rec.Cols[ci];
            if (unit1 == null) unit1 = c.Unit;
            else if (c.Unit != unit1 && unit2 == null) unit2 = c.Unit;
            if (c.Unit != unit1 && c.Unit != unit2) { skipped.Add(c.Name); continue; }
            Series s = new Series(c.Name);
            s.ChartType = SeriesChartType.Line; s.BorderWidth = 2; s.XValueType = ChartValueType.Double;
            s.YAxisType = c.Unit == unit1 ? AxisType.Primary : AxisType.Secondary;
            s.Color = SeriesColor(c.Name, chart.Series.Count);
            List<double> xs = new List<double>(), ys = new List<double>();
            Decimate(c.V, i0, i1, xs, ys);
            s.Points.DataBindXY(xs, ys);
            chart.Series.Add(s);
        }
        area.AxisY.Title = unit1 == null ? "" : "[" + unit1 + "]";
        area.AxisY2.Enabled = unit2 != null ? AxisEnabled.True : AxisEnabled.False;
        area.AxisY.Minimum = double.IsNaN(yLo) ? double.NaN : yLo;
        area.AxisY.Maximum = double.IsNaN(yLo) ? double.NaN : yHi;
        area.AxisY2.Title = unit2 == null ? "" : "[" + unit2 + "]";
        area.AxisX.Minimum = ToX(viewStart); area.AxisX.Maximum = ToX(viewEnd);
        // Etiket araligi: yuvarlak bir adim; saat modunda saat baslarina, sure modunda kayit basina hizali
        double step = NiceStep((viewEnd - viewStart) / 8);
        double origin = ClockAxis ? rec.Start.TimeOfDay.TotalSeconds : 0;
        double firstTick = Math.Ceiling((viewStart + origin) / step) * step - origin;
        area.AxisX.Interval = step; area.AxisX.IntervalOffset = firstTick - viewStart;
        area.AxisX.MajorGrid.Interval = step; area.AxisX.MajorGrid.IntervalOffset = firstTick - viewStart;
        area.AxisX.MajorTickMark.Interval = step; area.AxisX.MajorTickMark.IntervalOffset = firstTick - viewStart;
        area.AxisX.LabelStyle.Interval = step; area.AxisX.LabelStyle.IntervalOffset = firstTick - viewStart;
        axisStep = step;

        // Olaylar: aralik olaylari golgeli serit, anlik olaylar dikey cizgi (yalnizca gorunen aralikta, en fazla 400)
        area.AxisX.StripLines.Clear();
        int n = 0;
        foreach (Ev e in shown)
        {
            double d = double.IsNaN(e.Dur) ? 0 : e.Dur;
            if (e.T + d < viewStart || e.T > viewEnd) continue;
            if (++n > 400) break;
            StripLine sl = new StripLine();
            Color col = KindColor(e.Kind);
            sl.IntervalOffset = e.T; // tekrarsiz serit: eksen uzerindeki mutlak konum (saniye)
            sl.StripWidth = d;
            if (d > 0) sl.BackColor = Color.FromArgb(60, col);
            sl.BorderColor = col; sl.BorderWidth = 1; sl.BorderDashStyle = d > 0 ? ChartDashStyle.Solid : ChartDashStyle.Dash;
            area.AxisX.StripLines.Add(sl);
        }

        // Limit analizi cizgileri: analiz edilen seri sol eksendeyse
        area.AxisY.StripLines.Clear();
        if (calcEvents.Count > 0 || lblSummary.Text.Length > 0)
        {
            Column lc = rec.Cols[cmbLimit.SelectedIndex];
            if (lc.Unit == unit1 && lstCols.GetItemChecked(cmbLimit.SelectedIndex))
                foreach (double lim in new double[] { Fmt.Parse(txtLow.Text), Fmt.Parse(txtHigh.Text) })
                {
                    if (double.IsNaN(lim)) continue;
                    StripLine sl = new StripLine();
                    sl.IntervalOffset = lim; sl.StripWidth = 0;
                    sl.BorderColor = Ui.Th.Bad; sl.BorderWidth = 1; sl.BorderDashStyle = ChartDashStyle.Dash;
                    area.AxisY.StripLines.Add(sl);
                }
        }
        area.RecalculateAxesScale();
        stView.Text = Ui.S("Görünen: ", "Showing: ") + Dur(viewEnd - viewStart) + " / " + Dur(T1 - T0) + "   ·   " + rec.T.Length + Ui.S(" okuma", " readings")
            + (skipped.Count > 0 ? "   ·   " + Ui.S("çizilmeyen (üçüncü birim): ", "not drawn (third unit): ") + string.Join(", ", skipped.ToArray()) : "");
    }

    static Color SeriesColor(string name, int index)
    {
        // "C1 RMS" gibi adlarda kanal rengi; ayni kanaldan birden cok seri varsa sirayla farkli tonlar
        Color[] extra = { Color.FromArgb(120, 144, 220), Color.FromArgb(230, 140, 0), Color.FromArgb(160, 110, 200), Color.FromArgb(120, 170, 60) };
        if (index < 4 && name.Length >= 2 && name[0] == 'C' && name[1] >= '1' && name[1] <= '4' && index == 0) return Ui.Th.Chan[name[1] - '1'];
        return index < 4 ? Ui.Th.Chan[index] : extra[index % extra.Length];
    }

    // Cok nokta varsa her dilimin en dusuk ve en yuksek degerini birakir; kisa sureli olaylar kaybolmaz
    void Decimate(double[] V, int i0, int i1, List<double> x, List<double> y)
    {
        int n = i1 - i0;
        int step = n <= MaxBuckets * 2 ? 1 : n / MaxBuckets + 1;
        for (int i = i0; i < i1; i += step)
        {
            int lo = -1, hi = -1;
            for (int j = i; j < Math.Min(i + step, i1); j++)
            {
                if (double.IsNaN(V[j])) continue;
                if (lo < 0 || V[j] < V[lo]) lo = j;
                if (hi < 0 || V[j] > V[hi]) hi = j;
            }
            if (lo < 0) continue;
            int a = Math.Min(lo, hi), b = Math.Max(lo, hi);
            x.Add(ToX(rec.T[a])); y.Add(V[a]);
            if (b != a) { x.Add(ToX(rec.T[b])); y.Add(V[b]); }
        }
    }

    double CursorSeconds(int pixelX)
    {
        try { return ToSec(chart.ChartAreas[0].AxisX.PixelPositionToValue(pixelX)); }
        catch (Exception) { return double.NaN; } // grafik henuz cizilmedi ya da fare alanin disinda
    }

    // Farenin altindaki an: saat ve o andaki degerler durum cubugunda
    void ShowCursor(int pixelX)
    {
        if (rec == null || rec.T.Length == 0) return;
        double t = CursorSeconds(pixelX);
        if (double.IsNaN(t) || t < viewStart || t > viewEnd) { stCursor.Text = ""; lblHover.Visible = false; return; }
        int i = Array.BinarySearch(rec.T, t);
        if (i < 0) i = Math.Min(~i, rec.T.Length - 1);
        if (i > 0 && Math.Abs(rec.T[i - 1] - t) < Math.Abs(rec.T[i] - t)) i--;
        StringBuilder sb = new StringBuilder();
        sb.Append(Ui.S("Kayıt süresi ", "Elapsed ") + Fmt.Span(rec.T[i]));
        if (rec.HasClock) sb.Append("   (" + rec.Start.AddSeconds(rec.T[i]).ToString("dd.MM.yyyy HH:mm:ss") + ")");
        for (int ci = 0; ci < rec.Cols.Count; ci++)
            if (lstCols.GetItemChecked(ci)) sb.Append("     " + rec.Cols[ci].Name + " = " + Fmt.Eng(rec.Cols[ci].V[i], rec.Cols[ci].Unit));
        stCursor.Text = sb.ToString();

        // ayni bilgi farenin sag ustunde, satir satir
        StringBuilder tip = new StringBuilder(Fmt.Span(rec.T[i]));
        if (rec.HasClock) tip.Append("   " + rec.Start.AddSeconds(rec.T[i]).ToString("HH:mm:ss"));
        for (int ci = 0; ci < rec.Cols.Count; ci++)
            if (lstCols.GetItemChecked(ci)) tip.Append("\n" + rec.Cols[ci].Name + "  " + Fmt.Eng(rec.Cols[ci].V[i], rec.Cols[ci].Unit));
        HoverTip.Show(lblHover, tip.ToString(), hoverPt);
    }

    // --selftest <png> <dosyalar...> [--limit seri alt ust] [--event n]: dosyalari acar, pencere goruntusunu kaydedip kapanir (gelistirme icin)
    public bool TestClock; // --clock: zaman eksenini saat moduna alir
    public bool TestZoomY; // --zoomy: deger ekseninde yakinlastirilmis gorunum
    public void SelfTest(string png, List<string> files, string[] limit, int eventIndex)
    {
        Shown += delegate
        {
            OpenFiles(files.ToArray());
            if (limit != null && rec != null)
            {
                cmbLimit.SelectedIndex = int.Parse(limit[0]); txtLow.Text = limit[1]; txtHigh.Text = limit[2];
                Analyze();
            }
            if (TestClock) cmbAxis.SelectedIndex = 1;
            if (eventIndex >= 0 && eventIndex < lvEvents.Items.Count) lvEvents.Items[eventIndex].Selected = true;
            if (TestZoomY) { Axis zy = chart.ChartAreas[0].AxisY; chart.Update(); double zl = zy.Minimum, zh = zy.Maximum; yLo = 0.45; yHi = 0.55; Redraw(); }
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1500;
            t.Tick += delegate
            {
                t.Stop();
                hoverPt = new Point(chart.Width / 2, chart.Height / 2);
                ShowCursor(chart.Width / 2);
                TopMost = true; Activate(); Refresh(); Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
                using (Bitmap b = new Bitmap(Width, Height))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(Location, Point.Empty, Size);
                    b.Save(png);
                }
                Close();
            };
            t.Start();
        };
    }
}

static class ViewerProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ViewerForm f = new ViewerForm();
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            List<string> files = new List<string>();
            string[] limit = null; int ev = -1;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--limit" && i + 3 < args.Length) { limit = new string[] { args[i + 1], args[i + 2], args[i + 3] }; i += 3; }
                else if (args[i] == "--event" && i + 1 < args.Length) ev = int.Parse(args[++i]);
                else if (args[i] == "--clock") f.TestClock = true;
                else if (args[i] == "--zoomy") f.TestZoomY = true;
                else files.Add(args[i]);
            }
            f.SelfTest(args[1], files, limit, ev);
        }
        else if (args.Length > 0) f.Shown += delegate { f.OpenFiles(args); }; // dosyalar exe'nin uzerine birakilirsa
        Application.Run(f);
    }
}
