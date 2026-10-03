// OsiloTakip.exe - pencereli olcum uygulamasi. Derleme icin: derle.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

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

class ParamInfo
{
    public string Code, Unit;
    readonly string nameTr, nameEn;
    public string Name { get { return Ui.S(nameTr, nameEn); } }
    public ParamInfo(string code, string tr, string en, string unit) { Code = code; nameTr = tr; nameEn = en; Unit = unit; }
    public override string ToString() { return Code + "  –  " + Name; }

    public static readonly ParamInfo[] All = {
        new ParamInfo("RMS", "etkin değer", "RMS value", "V"),
        new ParamInfo("MEAN", "ortalama (DC)", "mean (DC)", "V"),
        new ParamInfo("PKPK", "tepe-tepe (Vpp)", "peak-to-peak (Vpp)", "V"),
        new ParamInfo("MAX", "en yüksek", "maximum", "V"),
        new ParamInfo("MIN", "en düşük", "minimum", "V"),
        new ParamInfo("AMPL", "genlik", "amplitude", "V"),
        new ParamInfo("TOP", "üst seviye", "top level", "V"),
        new ParamInfo("BASE", "alt seviye", "base level", "V"),
        new ParamInfo("CRMS", "çevrim RMS", "cycle RMS", "V"),
        new ParamInfo("CMEAN", "çevrim ortalaması", "cycle mean", "V"),
        new ParamInfo("FREQ", "frekans", "frequency", "Hz"),
        new ParamInfo("PER", "periyot", "period", "s"),
        new ParamInfo("DUTY", "doluluk oranı", "duty cycle", "%"),
        new ParamInfo("PWID", "pozitif darbe genişliği", "positive pulse width", "s"),
        new ParamInfo("NWID", "negatif darbe genişliği", "negative pulse width", "s"),
        new ParamInfo("RISE", "yükselme süresi", "rise time", "s"),
        new ParamInfo("FALL", "düşme süresi", "fall time", "s"),
    };

    public static ParamInfo Find(string code)
    {
        foreach (ParamInfo p in All) if (p.Code == code) return p;
        return null;
    }
}

// Bir kanal+parametre ciftinin olcum gecmisi ve istatistikleri. Is parcacigi yazar, arayuz okur (kilit: MainForm.lk).
class SeriesData
{
    public string Ch;
    public ParamInfo P;
    public List<double> T = new List<double>(), V = new List<double>();
    public double Last = double.NaN, Min, Max, Sum;
    public long N;
    public static int Cap = 200000; // seri basina saklanan en fazla nokta; dolunca en eski %10 atilir

    public SeriesData(string ch, ParamInfo p) { Ch = ch; P = p; ResetStats(); }
    public string Key { get { return Ch + "_" + P.Code; } }

    public void ResetStats() { Min = double.MaxValue; Max = double.MinValue; Sum = 0; N = 0; }

    public void Add(double t, double v)
    {
        if (T.Count >= Cap) { T.RemoveRange(0, Cap / 10); V.RemoveRange(0, Cap / 10); }
        T.Add(t); V.Add(v);
        Last = v;
        if (double.IsNaN(v)) return;
        if (v < Min) Min = v;
        if (v > Max) Max = v;
        Sum += v; N++;
    }
}

class Config
{
    public List<string> Chans = new List<string>();
    public List<ParamInfo> Params = new List<ParamInfo>();
    public int Interval;
    public int AlarmIdx = -1;          // series icindeki sira, -1 = kapali
    public double Low = double.NaN, High = double.NaN;
    public bool Beep;
    public bool Lan;                   // false = USB (WinUSB), true = ag
    public string Addr = "";
    public int DialectIdx;             // Dialect.Names icindeki sira
}

class MainForm : Form
{
    static readonly CultureInfo Cur = CultureInfo.CurrentCulture;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string Sep = Cur.TextInfo.ListSeparator;
    static readonly string[] Chans = { "C1", "C2", "C3", "C4" };
    // Sorgular arka arkaya gelirse SDS1104X-E yeni yakalama yapamiyor (ekran donuyor, deger yenilenmiyor); olculen esik ~30 ms
    const int MinInterval = 40;

    readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    string RecDir { get { return Path.Combine(baseDir, "kayitlar"); } }
    string IniPath { get { return Path.Combine(baseDir, "ayarlar.ini"); } }

    // arayuz
    Button btnStart, btnFolder, btnShot, btnReset;
    CheckBox chkRecord, chkAlarm, chkBeep;
    CheckBox[] chkChan = new CheckBox[4];
    CheckedListBox lstParams;
    NumericUpDown numInterval;
    ComboBox cmbAlarmCh, cmbAlarmParam, cmbChart, cmbWindow, cmbConn, cmbDialect, cmbLang, cmbTheme;
    Panel topBar;
    StatusStrip status;
    bool uiRunning;
    TextBox txtLow, txtHigh, txtAddr;
    Label lblLow, lblHigh, lblDevice;
    FlowLayoutPanel cards;
    Chart chart;
    HScrollBar scroll;
    Label lblHistory;
    bool follow = true; // grafik en yeni veriyi izliyor
    static readonly int[] WindowSecs = { 30, 120, 600, 1800, 3600, 6 * 3600, 12 * 3600, 24 * 3600, 3 * 24 * 3600 };
    ListBox lstLog;
    ToolStripStatusLabel stState, stCount, stRate, stTime, stFile, stAlarm;
    System.Windows.Forms.Timer uiTimer;
    Panel[] cardPanel; Label[] cardValue, cardStat;
    Control[] lockWhileRunning;

    // is parcacigi ile paylasilan durum
    readonly object lk = new object();
    List<SeriesData> series = new List<SeriesData>();
    readonly Queue<string> logQueue = new Queue<string>();
    Thread worker;
    Config cfg;
    volatile bool running, wantRecord, connected, alarmOut;
    volatile string deviceText = "", shotRequest, recFile = "";
    double lastT;
    long samples, violations;
    DateTime startedAt;

    public MainForm()
    {
        Text = "OsiloTakip";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        LoadPrefs();
        BuildUi();
        LoadSettings();
        UpdateLimitLabels();
        uiTimer = new System.Windows.Forms.Timer();
        uiTimer.Interval = 250;
        uiTimer.Tick += delegate { RefreshUi(); };
        uiTimer.Start();
        FormClosing += delegate { StopMeasure(); SaveSettings(); };
        Shown += delegate { Config c = ReadConn(); ThreadPool.QueueUserWorkItem(delegate { if (!running) WithDevice(c, null); }); };
    }

    // ---------------------------------------------------------------- arayuz kurulumu

    void BuildUi()
    {
        TableLayoutPanel main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.ColumnCount = 1; main.RowCount = 5;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        main.Padding = new Padding(4);

        cards = new FlowLayoutPanel();
        cards.Dock = DockStyle.Fill; cards.AutoScroll = true;
        main.Controls.Add(cards, 0, 0);

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Fill;
        bar.Controls.Add(MakeLabel(Ui.S("Grafik:", "Chart:"), 6));
        cmbChart = MakeCombo(190);
        foreach (ParamInfo p in ParamInfo.All) cmbChart.Items.Add(p);
        cmbChart.SelectedIndex = 0;
        bar.Controls.Add(cmbChart);
        bar.Controls.Add(MakeLabel(Ui.S("Zaman aralığı:", "Time span:"), 6));
        cmbWindow = MakeCombo(100);
        cmbWindow.Items.AddRange(new object[] { Ui.S("30 sn", "30 s"), Ui.S("2 dk", "2 min"), Ui.S("10 dk", "10 min"), Ui.S("30 dk", "30 min"), Ui.S("1 saat", "1 hour"),
            Ui.S("6 saat", "6 hours"), Ui.S("12 saat", "12 hours"), Ui.S("1 gün", "1 day"), Ui.S("3 gün", "3 days") });
        cmbWindow.SelectedIndex = 1;
        bar.Controls.Add(cmbWindow);
        lblHistory = MakeLabel(Ui.S("◀ Geçmişe bakılıyor – canlı veri için çubuğu en sağa çekin", "◀ Viewing history – drag the bar fully right for live data"), 6);
        lblHistory.ForeColor = Ui.Th.Bad; lblHistory.Visible = false;
        bar.Controls.Add(lblHistory);
        main.Controls.Add(bar, 0, 1);

        chart = new Chart();
        chart.Dock = DockStyle.Fill;
        ChartArea area = new ChartArea("a");
        area.AxisX.Title = Ui.S("Süre [s]", "Time [s]");
        area.AxisX.LabelStyle.Format = "0";
        area.AxisX.MajorGrid.LineColor = Ui.Th.Grid;
        area.AxisY.MajorGrid.LineColor = Ui.Th.Grid;
        area.AxisY.IsStartedFromZero = false;
        area.AxisY.LabelStyle.Format = "0.###";
        chart.ChartAreas.Add(area);
        Legend lg = new Legend("l");
        lg.Docking = Docking.Top;
        chart.Legends.Add(lg);
        main.Controls.Add(chart, 0, 2);

        scroll = new HScrollBar();
        scroll.Dock = DockStyle.Fill; scroll.Enabled = false;
        scroll.ValueChanged += delegate { follow = scroll.Value >= scroll.Maximum - scroll.LargeChange + 1; };
        main.Controls.Add(scroll, 0, 3);

        lstLog = new ListBox();
        lstLog.Dock = DockStyle.Fill; lstLog.IntegralHeight = false;
        main.Controls.Add(lstLog, 0, 4);

        // sol panel
        Panel left = new Panel();
        left.Dock = DockStyle.Left; left.Width = 250; left.AutoScroll = true;
        left.Padding = new Padding(6);
        int y = 6;

        GroupBox gConn = MakeGroup(Ui.S("Bağlantı", "Connection"), left, ref y, 112);
        cmbConn = MakeCombo(70); cmbConn.Items.AddRange(new object[] { "USB", Ui.S("Ağ", "LAN") }); cmbConn.SelectedIndex = 0;
        cmbConn.SetBounds(12, 22, 70, 24);
        txtAddr = new TextBox(); txtAddr.SetBounds(88, 22, 138, 24);
        cmbConn.SelectedIndexChanged += delegate { txtAddr.Enabled = cmbConn.SelectedIndex == 1; };
        txtAddr.Enabled = false;
        Label lAddr = MakeLabel(Ui.S("Ağ için IP adresi (örn. 192.168.1.50:5025)", "IP address for LAN (e.g. 192.168.1.50:5025)"), 0);
        lAddr.Font = new Font("Segoe UI", 7.5f); lAddr.ForeColor = Ui.Th.Muted;
        lAddr.SetBounds(12, 50, 216, 16);
        Label lDia = MakeLabel(Ui.S("Komut seti:", "Command set:"), 0); lDia.SetBounds(12, 78, 90, 20);
        cmbDialect = MakeCombo(140); cmbDialect.Items.AddRange(Dialect.Names); cmbDialect.Items[0] = Ui.S("Otomatik", "Automatic"); cmbDialect.SelectedIndex = 0;
        cmbDialect.SetBounds(104, 74, 122, 24);
        gConn.Controls.AddRange(new Control[] { cmbConn, txtAddr, lAddr, lDia, cmbDialect });

        GroupBox gCh = MakeGroup(Ui.S("Kanallar", "Channels"), left, ref y, 52);
        for (int i = 0; i < 4; i++)
        {
            chkChan[i] = new CheckBox();
            chkChan[i].Text = Chans[i]; chkChan[i].ForeColor = Ui.Th.Chan[i];
            chkChan[i].Font = new Font(Font, FontStyle.Bold);
            chkChan[i].SetBounds(12 + i * 56, 22, 52, 22);
            gCh.Controls.Add(chkChan[i]);
        }
        chkChan[0].Checked = true;

        GroupBox gPar = MakeGroup(Ui.S("Ölçülecek parametreler", "Parameters to measure"), left, ref y, 190);
        lstParams = new CheckedListBox();
        lstParams.CheckOnClick = true; lstParams.IntegralHeight = false;
        lstParams.SetBounds(8, 22, 218, 160);
        foreach (ParamInfo p in ParamInfo.All) lstParams.Items.Add(p);
        lstParams.SetItemChecked(0, true);
        gPar.Controls.Add(lstParams);

        GroupBox gInt = MakeGroup(Ui.S("Okuma aralığı", "Read interval"), left, ref y, 56);
        numInterval = new NumericUpDown();
        numInterval.Minimum = MinInterval; numInterval.Maximum = 60000; numInterval.Increment = 10; numInterval.Value = MinInterval;
        numInterval.SetBounds(12, 22, 80, 24);
        gInt.Controls.Add(numInterval);
        Label ms = MakeLabel(Ui.S("ms bekleme (en az ", "ms wait (min ") + MinInterval + ")", 0);
        ms.SetBounds(98, 25, 130, 20);
        gInt.Controls.Add(ms);

        GroupBox gAl = MakeGroup(Ui.S("Limit kontrolü (süreklilik)", "Limit check (continuity)"), left, ref y, 176);
        chkAlarm = new CheckBox(); chkAlarm.Text = Ui.S("Limit dışına çıkınca uyar", "Warn when out of limits");
        chkAlarm.SetBounds(12, 22, 210, 22);
        cmbAlarmCh = MakeCombo(56); cmbAlarmCh.Items.AddRange(Chans); cmbAlarmCh.SelectedIndex = 0;
        cmbAlarmCh.SetBounds(12, 48, 56, 24);
        cmbAlarmParam = MakeCombo(150);
        foreach (ParamInfo p in ParamInfo.All) cmbAlarmParam.Items.Add(p);
        cmbAlarmParam.SelectedIndex = 0;
        cmbAlarmParam.SetBounds(74, 48, 152, 24);
        cmbAlarmParam.SelectedIndexChanged += delegate { UpdateLimitLabels(); };
        lblLow = MakeLabel("", 0); lblLow.SetBounds(12, 82, 100, 20);
        txtLow = new TextBox(); txtLow.SetBounds(116, 79, 110, 24);
        lblHigh = MakeLabel("", 0); lblHigh.SetBounds(12, 112, 100, 20);
        txtHigh = new TextBox(); txtHigh.SetBounds(116, 109, 110, 24);
        chkBeep = new CheckBox(); chkBeep.Text = Ui.S("Sesli uyarı", "Sound alert"); chkBeep.Checked = true;
        chkBeep.SetBounds(12, 142, 210, 22);
        gAl.Controls.AddRange(new Control[] { chkAlarm, cmbAlarmCh, cmbAlarmParam, lblLow, txtLow, lblHigh, txtHigh, chkBeep });



        // ust cubuk
        Panel top = new Panel();
        top.Dock = DockStyle.Top; top.Height = 58; top.BackColor = Ui.Th.Bar;
        btnStart = new Button();
        btnStart.SetBounds(10, 9, 150, 40);
        btnStart.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        btnStart.FlatStyle = FlatStyle.Flat; btnStart.ForeColor = Color.White;
        btnStart.Click += delegate { if (running) StopMeasure(); else StartMeasure(); };
        chkRecord = new CheckBox(); chkRecord.Text = Ui.S("CSV dosyasına kaydet", "Record to CSV file"); chkRecord.Checked = true;
        chkRecord.SetBounds(176, 19, 160, 22);
        chkRecord.CheckedChanged += delegate { wantRecord = chkRecord.Checked; };
        btnFolder = MakeButton(Ui.S("Kayıt klasörü", "Records folder"), 340, delegate { Directory.CreateDirectory(RecDir); Process.Start(RecDir); });
        btnShot = MakeButton(Ui.S("Ekran görüntüsü al", "Take screenshot"), 456, delegate { TakeShot(); });
        btnShot.Width = 130;
        btnReset = MakeButton(Ui.S("İstatistiği sıfırla", "Reset statistics"), 592, delegate { lock (lk) { foreach (SeriesData s in series) s.ResetStats(); violations = 0; } });
        btnReset.Width = 120;
        lblDevice = new Label();
        lblDevice.Dock = DockStyle.Right; lblDevice.Width = 270;
        lblDevice.Padding = new Padding(0, 0, 10, 0);
        lblDevice.TextAlign = ContentAlignment.MiddleRight;
        // dil ve tema: degisince arayuz bastan kurulur (olcum surerken kilitli)
        cmbLang = MakeCombo(78); cmbLang.Items.AddRange(new object[] { "Türkçe", "English" });
        cmbLang.SelectedIndex = Ui.En ? 1 : 0;
        cmbLang.SetBounds(724, 17, 78, 24);
        cmbLang.SelectedIndexChanged += delegate { Ui.En = cmbLang.SelectedIndex == 1; BeginInvoke(new MethodInvoker(Rebuild)); };
        cmbTheme = MakeCombo(74); cmbTheme.Items.AddRange(new object[] { Ui.S("Açık", "Light"), Ui.S("Koyu", "Dark") });
        cmbTheme.SelectedIndex = Ui.Th.Dark ? 1 : 0;
        cmbTheme.SetBounds(808, 17, 74, 24);
        cmbTheme.SelectedIndexChanged += delegate { Ui.Th = cmbTheme.SelectedIndex == 1 ? Theme.MakeDark() : Theme.Light(); BeginInvoke(new MethodInvoker(Rebuild)); };
        top.Controls.AddRange(new Control[] { btnStart, chkRecord, btnFolder, btnShot, btnReset, cmbLang, cmbTheme, lblDevice });
        topBar = top;

        StatusStrip st = new StatusStrip();
        status = st;
        stState = new ToolStripStatusLabel(Ui.S("Hazır", "Ready"));
        stCount = new ToolStripStatusLabel(""); stRate = new ToolStripStatusLabel("");
        stTime = new ToolStripStatusLabel(""); stAlarm = new ToolStripStatusLabel("");
        stFile = new ToolStripStatusLabel(""); stFile.Spring = true; stFile.TextAlign = ContentAlignment.MiddleRight;
        foreach (ToolStripStatusLabel l in new ToolStripStatusLabel[] { stState, stCount, stRate, stTime, stAlarm })
        { l.BorderSides = ToolStripStatusLabelBorderSides.Right; l.Padding = new Padding(4, 0, 4, 0); }
        st.Items.AddRange(new ToolStripItem[] { stState, stCount, stRate, stTime, stAlarm, stFile });

        // Dock sirasi: son eklenen once yerlesir
        Controls.Add(main);
        Controls.Add(left);
        Controls.Add(top);
        Controls.Add(st);
        wantRecord = true;
        SetStartButton();
        // Etiketler acik kalir (koyu temada devre disi yazi okunmuyor); yalnizca girdi denetimleri kilitlenir
        List<Control> locks = new List<Control>();
        foreach (GroupBox g in new GroupBox[] { gConn, gCh, gPar, gInt, gAl })
            foreach (Control c in g.Controls) if (!(c is Label)) locks.Add(c);
        locks.Add(cmbLang); locks.Add(cmbTheme);
        lockWhileRunning = locks.ToArray();
        ApplyTheme();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar();
    }

    void DarkTitleBar()
    {
        // Windows 10 2004+ / 11: baslik cubugunu temaya uydurur; eski surumlerde sessizce etkisiz kalir
        int on = Ui.Th.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }
    }

    void ApplyTheme()
    {
        Theme t = Ui.Th;
        BackColor = t.Back; ForeColor = t.Text; // panel, etiket, onay kutusu gibi denetimler bunlari devralir
        topBar.BackColor = t.Bar;
        PaintInputs(this);
        btnStart.ForeColor = Color.White; btnStart.FlatAppearance.BorderSize = 0;
        for (int i = 0; i < 4; i++) chkChan[i].ForeColor = t.Chan[i];

        status.BackColor = t.Bar; status.SizingGrip = false;
        status.Renderer = new FlatStripRenderer();
        foreach (ToolStripItem it in status.Items) it.ForeColor = t.Text;

        chart.BackColor = t.Back;
        ChartArea a = chart.ChartAreas[0];
        a.BackColor = t.Plot;
        foreach (Axis ax in new Axis[] { a.AxisX, a.AxisY })
        {
            ax.LineColor = t.Border; ax.MajorTickMark.LineColor = t.Border; ax.MajorGrid.LineColor = t.Grid;
            ax.LabelStyle.ForeColor = t.Text; ax.TitleForeColor = t.Text;
        }
        chart.Legends[0].BackColor = Color.Transparent; chart.Legends[0].ForeColor = t.Text;
        if (t.Dark) try { SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        if (IsHandleCreated) DarkTitleBar();
    }

    void DrawComboItem(object sender, DrawItemEventArgs e)
    {
        ComboBox c = (ComboBox)sender;
        Theme t = Ui.Th;
        bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (SolidBrush b = new SolidBrush(hot ? t.Border : t.Input)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, e.Bounds, c.Enabled ? t.Text : t.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    void PaintInputs(Control parent)
    {
        Theme t = Ui.Th;
        foreach (Control c in parent.Controls)
        {
            if (c is TextBox || c is ListBox || c is NumericUpDown || c is ComboBox)
            {
                c.BackColor = t.Input; c.ForeColor = t.Text;
                if (t.Dark)
                {
                    if (c is ComboBox)
                    {
                        // gorsel stil arka plan rengini yok saydigi icin duz stil + kendi cizimimiz
                        ComboBox cb = (ComboBox)c;
                        cb.FlatStyle = FlatStyle.Flat; cb.DrawMode = DrawMode.OwnerDrawFixed;
                        cb.DrawItem += DrawComboItem;
                    }
                    if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                    if (c is ListBox) { ((ListBox)c).BorderStyle = BorderStyle.FixedSingle; try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { } }
                }
            }
            else if (c is Button && c != btnStart && t.Dark)
            {
                Button b = (Button)c;
                b.FlatStyle = FlatStyle.Flat; b.BackColor = t.Input; b.ForeColor = t.Text;
                b.FlatAppearance.BorderColor = t.Border;
            }

            PaintInputs(c);
        }
    }

    // Dil ya da tema degisince arayuzu bastan kurar; olcum verisi ve olay listesi korunur
    void Rebuild()
    {
        if (running) return;
        SaveSettings();
        object[] log = new object[lstLog.Items.Count];
        lstLog.Items.CopyTo(log, 0);
        SuspendLayout();
        List<Control> old = new List<Control>();
        foreach (Control c in Controls) old.Add(c);
        Controls.Clear();
        foreach (Control c in old) c.Dispose();
        cardPanel = null; cardValue = null; cardStat = null;
        BuildUi();
        LoadSettings();
        UpdateLimitLabels();
        lstLog.Items.AddRange(log);
        if (series.Count > 0) BuildCards();
        ResumeLayout();
    }

    // Dil ve tema arayuz kurulmadan once okunur; kayit yoksa dil Windows diline gore secilir
    void LoadPrefs()
    {
        Ui.En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr";
        if (!File.Exists(IniPath)) return;
        foreach (string line in File.ReadAllLines(IniPath))
        {
            if (line == "dil=en") Ui.En = true;
            else if (line == "dil=tr") Ui.En = false;
            else if (line == "tema=koyu") Ui.Th = Theme.MakeDark();
        }
    }

    Label MakeLabel(string text, int topMargin)
    {
        Label l = new Label();
        l.Text = text; l.AutoSize = true; l.Margin = new Padding(6, topMargin, 0, 0);
        return l;
    }

    ComboBox MakeCombo(int width)
    {
        ComboBox c = new ComboBox();
        c.DropDownStyle = ComboBoxStyle.DropDownList; c.Width = width;
        return c;
    }

    Button MakeButton(string text, int x, EventHandler click)
    {
        Button b = new Button();
        b.Text = text; b.SetBounds(x, 14, 110, 30);
        b.Click += click;
        return b;
    }

    GroupBox MakeGroup(string text, Panel parent, ref int y, int height)
    {
        GroupBox g = new ThemedGroup();
        g.Text = text; g.SetBounds(6, y, 234, height);
        parent.Controls.Add(g);
        y += height + 8;
        return g;
    }

    void SetStartButton()
    {
        btnStart.Text = running ? Ui.S("■  Durdur", "■  Stop") : Ui.S("▶  Başlat", "▶  Start");
        btnStart.BackColor = running ? Color.FromArgb(198, 40, 40) : Color.FromArgb(46, 125, 50);
    }

    void UpdateLimitLabels()
    {
        string unit = ((ParamInfo)cmbAlarmParam.SelectedItem).Unit;
        lblLow.Text = Ui.S("Alt limit [", "Lower limit [") + unit + "]:";
        lblHigh.Text = Ui.S("Üst limit [", "Upper limit [") + unit + "]:";
    }

    // ---------------------------------------------------------------- bicimlendirme

    // 0.01166 V -> "11,66 mV"
    static string Eng(double v, string unit)
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
    static string Plain(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return "";
        if (x == 0) return "0";
        int dec = digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(x)));
        decimal d = dec > 28 ? 0m : Math.Round((decimal)x, Math.Max(dec, 0));
        return d.ToString("0.############################", Cur);
    }

    static double ParseLimit(string s)
    {
        double v;
        s = s.Trim().Replace(',', '.');
        return s.Length > 0 && double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN;
    }

    static string Span(double sec)
    {
        TimeSpan t = TimeSpan.FromSeconds(sec);
        return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
    }

    void Log(string msg)
    {
        lock (logQueue) logQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
    }

    // ---------------------------------------------------------------- baslat / durdur

    void StartMeasure()
    {
        Config c = ReadConn();
        if (c.Lan && c.Addr.Length == 0)
        {
            MessageBox.Show(this, Ui.S("Ağ bağlantısı için osiloskobun IP adresini girin.", "Enter the oscilloscope's IP address for the LAN connection."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        for (int i = 0; i < 4; i++) if (chkChan[i].Checked) c.Chans.Add(Chans[i]);
        foreach (object o in lstParams.CheckedItems) c.Params.Add((ParamInfo)o);
        if (c.Chans.Count == 0 || c.Params.Count == 0)
        {
            MessageBox.Show(this, Ui.S("En az bir kanal ve bir parametre seçin.", "Select at least one channel and one parameter."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        c.Interval = (int)numInterval.Value;
        c.Beep = chkBeep.Checked;
        List<SeriesData> list = new List<SeriesData>();
        foreach (string ch in c.Chans) foreach (ParamInfo p in c.Params) list.Add(new SeriesData(ch, p));
        if (chkAlarm.Checked)
        {
            c.Low = ParseLimit(txtLow.Text); c.High = ParseLimit(txtHigh.Text);
            string key = (string)cmbAlarmCh.SelectedItem + "_" + ((ParamInfo)cmbAlarmParam.SelectedItem).Code;
            c.AlarmIdx = list.FindIndex(delegate(SeriesData s) { return s.Key == key; });
            if (c.AlarmIdx < 0 || (double.IsNaN(c.Low) && double.IsNaN(c.High)))
            {
                MessageBox.Show(this, Ui.S("Limit kontrolü için seçilen kanal ve parametre ölçülenler arasında olmalı ve en az bir limit girilmeli.", "The channel and parameter chosen for the limit check must be among the measured ones, and at least one limit must be entered."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        // 3 gunluk gecmis icin nokta siniri: tek seride ~2,6 milyon (8 okuma/sn x 3 gun), cok seride bellek icin bolusturulur
        SeriesData.Cap = Math.Max(200000, 6000000 / list.Count);
        follow = true;
        lock (lk) { series = list; samples = 0; violations = 0; lastT = 0; }
        cfg = c;
        alarmOut = false;
        // grafikte secili parametre olculmuyorsa ilk olculene gec
        if (!c.Params.Contains((ParamInfo)cmbChart.SelectedItem)) cmbChart.SelectedItem = c.Params[0];
        BuildCards();
        startedAt = DateTime.Now;
        running = true; uiRunning = true;
        wantRecord = chkRecord.Checked;
        foreach (Control ctl in lockWhileRunning) ctl.Enabled = false;
        SetStartButton();
        SaveSettings();
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Start();
    }

    void StopMeasure()
    {
        if (!running && !uiRunning) return;
        running = false;
        if (worker != null) worker.Join(6000);
        worker = null; uiRunning = false;
        foreach (Control ctl in lockWhileRunning) ctl.Enabled = true;
        txtAddr.Enabled = cmbConn.SelectedIndex == 1;
        SetStartButton();
    }

    void BuildCards()
    {
        cards.SuspendLayout();
        cards.Controls.Clear();
        int n = series.Count;
        cardPanel = new Panel[n]; cardValue = new Label[n]; cardStat = new Label[n];
        for (int i = 0; i < n; i++)
        {
            SeriesData s = series[i];
            Panel p = new Panel();
            p.Size = new Size(206, 108); p.Margin = new Padding(4); p.BackColor = Ui.Th.Card; p.BorderStyle = BorderStyle.FixedSingle;
            Label title = new Label();
            title.Text = s.Ch + "  " + s.P.Code + "  (" + s.P.Name + ")";
            title.ForeColor = Ui.Th.Chan[Array.IndexOf(Chans, s.Ch)];
            title.Font = new Font(Font, FontStyle.Bold);
            title.SetBounds(8, 6, 192, 18); title.AutoEllipsis = true;
            Label val = new Label();
            val.Font = new Font("Segoe UI", 19f, FontStyle.Bold);
            val.SetBounds(6, 24, 194, 40); val.Text = "—"; val.ForeColor = Ui.Th.Text;
            Label stat = new Label();
            stat.Font = new Font("Segoe UI", 8f); stat.ForeColor = Ui.Th.Muted;
            stat.SetBounds(8, 66, 192, 36);
            foreach (Label l in new Label[] { title, val, stat }) l.BackColor = Color.Transparent;
            p.Controls.AddRange(new Control[] { title, val, stat });
            cards.Controls.Add(p);
            cardPanel[i] = p; cardValue[i] = val; cardStat[i] = stat;
        }
        cards.ResumeLayout();
    }

    // ---------------------------------------------------------------- olcum is parcacigi

    static double ParseValue(string s)
    {
        Match m = Regex.Match(s, @"^\s*[-+]?\d+(\.\d+)?([eE][-+]?\d+)?");
        return m.Success ? double.Parse(m.Value, Inv) : double.NaN; // "****" = gecersiz olcum
    }

    // Baglanti ayarlarini arayuzden okur (arayuz is parcaciginda cagrilmali)
    Config ReadConn()
    {
        Config c = new Config();
        c.Lan = cmbConn.SelectedIndex == 1;
        c.Addr = txtAddr.Text.Trim();
        c.DialectIdx = cmbDialect.SelectedIndex;
        return c;
    }

    static ILink OpenLink(Config c)
    {
        if (c.Lan) return TcpLink.Open(c.Addr, 3000);
        return Usb.Open(3000);
    }

    // basePath uzantisizdir; uzanti markaya gore eklenir
    void Shot(ILink u, Dialect d, string basePath)
    {
        if (d.ScreenshotCmd == null) { Log(Ui.S("Ekran görüntüsü bu komut setinde (", "Screenshot is not supported by this command set (") + d.Name + Ui.S(") desteklenmiyor", ")")); return; }
        u.SetTimeout(15000);
        try { File.WriteAllBytes(basePath + d.ScreenshotExt, u.Query(d.ScreenshotCmd)); }
        finally { u.SetTimeout(3000); }
        Log(Ui.S("Ekran görüntüsü kaydedildi: ", "Screenshot saved: ") + Path.GetFileName(basePath + d.ScreenshotExt));
    }

    void Work()
    {
        Config c = cfg;
        ILink u = null;
        Dialect dl = null;
        List<string> codes = new List<string>();
        foreach (ParamInfo p in c.Params) codes.Add(p.Code);
        StreamWriter csv = null, evCsv = null;
        Stopwatch sw = Stopwatch.StartNew();
        int errs = 0;
        double outSince = 0;
        double[] vals = new double[series.Count];
        Log(Ui.S("Ölçüm başladı", "Measurement started"));
        try
        {
            while (running)
            {
                try
                {
                    if (u == null)
                    {
                        u = OpenLink(c);
                        if (u == null)
                        {
                            connected = false; deviceText = Ui.S("Osiloskop bulunamadı – bekleniyor…", "Oscilloscope not found – waiting…");
                            Thread.Sleep(1000);
                            continue;
                        }
                        u.Clear(); u.SetTimeout(3000);
                        string idn = u.QueryText("*IDN?");
                        dl = Dialect.Pick(c.DialectIdx, idn);
                        deviceText = ShortIdn(idn);
                        connected = true;
                        Log(Ui.S("Bağlandı: ", "Connected: ") + deviceText + Ui.S("  –  komut seti: ", "  –  command set: ") + dl.Name);
                    }
                    string shot = shotRequest;
                    if (shot != null) { shotRequest = null; Shot(u, dl, shot); }

                    int k = 0;
                    foreach (string ch in c.Chans)
                        foreach (double v in dl.Measure(u, Array.IndexOf(Chans, ch) + 1, codes)) vals[k++] = v;
                    errs = 0;
                    double t = sw.Elapsed.TotalSeconds;
                    lock (lk)
                    {
                        for (int i = 0; i < vals.Length; i++) series[i].Add(t, vals[i]);
                        lastT = t; samples++;
                    }

                    // kayit ac/kapa (calisirken de degistirilebilir)
                    if (wantRecord && csv == null)
                    {
                        Directory.CreateDirectory(RecDir);
                        string name = Path.Combine(RecDir, "olcum_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        csv = new StreamWriter(name + ".csv", false, new UTF8Encoding(true));
                        csv.AutoFlush = true;
                        StringBuilder h = new StringBuilder(Ui.S("zaman", "time") + Sep + "t[s]");
                        foreach (SeriesData s in series) h.Append(Sep + s.Key + "[" + s.P.Unit + "]");
                        csv.WriteLine(h);
                        if (c.AlarmIdx >= 0)
                        {
                            evCsv = new StreamWriter(name + "_olaylar.csv", false, new UTF8Encoding(true));
                            evCsv.AutoFlush = true;
                            evCsv.WriteLine(Ui.S("zaman", "time") + Sep + "t[s]" + Sep + Ui.S("olay", "event") + Sep + Ui.S("deger", "value") + Sep + Ui.S("limit disi sure[s]", "out of limit[s]"));
                        }
                        recFile = Path.GetFileName(name + ".csv");
                        Log(Ui.S("Kayıt: ", "Recording: ") + recFile);
                    }
                    else if (!wantRecord && csv != null)
                    {
                        csv.Close(); csv = null;
                        if (evCsv != null) { evCsv.Close(); evCsv = null; }
                        recFile = "";
                        Log(Ui.S("Kayıt durduruldu", "Recording stopped"));
                    }
                    string stamp = DateTime.Now.ToString("G", Cur) + Sep + t.ToString("F2", Cur);
                    if (csv != null)
                    {
                        StringBuilder row = new StringBuilder(stamp);
                        foreach (double v in vals) row.Append(Sep + Plain(v, 4));
                        csv.WriteLine(row);
                    }

                    if (c.AlarmIdx >= 0)
                    {
                        double v = vals[c.AlarmIdx];
                        SeriesData s = series[c.AlarmIdx];
                        // gecersiz olcum (sinyal yok) de limit disi sayilir
                        bool bad = double.IsNaN(v) || v < c.Low || v > c.High;
                        if (bad && !alarmOut)
                        {
                            alarmOut = true; outSince = t;
                            lock (lk) violations++;
                            Log(Ui.S("LİMİT DIŞI  ", "OUT OF LIMIT  ") + s.Ch + " " + s.P.Code + " = " + Eng(v, s.P.Unit));
                            if (evCsv != null) evCsv.WriteLine(stamp + Sep + Ui.S("limit disi", "out of limit") + Sep + Plain(v, 4) + Sep);
                            if (c.Beep) SystemSounds.Exclamation.Play();
                        }
                        else if (!bad && alarmOut)
                        {
                            alarmOut = false;
                            Log(Ui.S("Normale döndü  ", "Back to normal  ") + s.Ch + " " + s.P.Code + " = " + Eng(v, s.P.Unit) + Ui.S("  (limit dışı süre ", "  (out of limit for ") + (t - outSince).ToString("F1", Cur) + Ui.S(" sn)", " s)"));
                            if (evCsv != null) evCsv.WriteLine(stamp + Sep + Ui.S("normale dondu", "back to normal") + Sep + Plain(v, 4) + Sep + (t - outSince).ToString("F2", Cur));
                        }
                    }
                    Thread.Sleep(Math.Max(c.Interval, MinInterval));
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is TimeoutException)) throw;
                    errs++;
                    Log(Ui.S("İletişim hatası: ", "Communication error: ") + e.Message);
                    if (errs >= 3 && u != null)
                    {
                        u.Dispose(); u = null;
                        connected = false; deviceText = Ui.S("Bağlantı koptu – yeniden deneniyor…", "Connection lost – retrying…");
                        Thread.Sleep(1000);
                    }
                    else if (u != null)
                    {
                        try { u.Clear(); u.SetTimeout(3000); }
                        catch (IOException) { errs = 3; } // baglanti gitmis: sonraki turda yeniden ac
                    }
                }
            }
        }
        catch (Exception e) { Log(Ui.S("Beklenmeyen hata: ", "Unexpected error: ") + e.Message); running = false; }
        finally
        {
            if (csv != null) csv.Close();
            if (evCsv != null) evCsv.Close();
            if (u != null) u.Dispose();
            recFile = "";
            Log(Ui.S("Ölçüm durdu", "Measurement stopped"));
        }
    }

    static string ShortIdn(string idn)
    {
        string[] p = idn.Split(',');
        return p.Length >= 3 ? p[1] + Ui.S("  (seri no ", "  (serial no ") + p[2] + ")" : idn;
    }

    // Olcum calismiyorken cihaza kisa sureligine baglanir
    bool WithDevice(Config c, Action<ILink, Dialect> act)
    {
        try
        {
            using (ILink u = OpenLink(c))
            {
                if (u == null) { connected = false; deviceText = Ui.S("Osiloskop bulunamadı (bağlı mı, başka program kullanıyor mu?)", "Oscilloscope not found (is it connected, is another program using it?)"); return false; }
                u.Clear(); u.SetTimeout(3000);
                string idn = u.QueryText("*IDN?");
                deviceText = ShortIdn(idn);
                connected = true;
                if (act != null) act(u, Dialect.Pick(c.DialectIdx, idn));
                return true;
            }
        }
        catch (Exception e) { Log(Ui.S("Hata: ", "Error: ") + e.Message); return false; }
    }

    void TakeShot()
    {
        Directory.CreateDirectory(RecDir);
        string path = Path.Combine(RecDir, "ekran_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        if (running) { shotRequest = path; return; }
        Config c = ReadConn();
        ThreadPool.QueueUserWorkItem(delegate { WithDevice(c, delegate(ILink u, Dialect d) { Shot(u, d, path); }); });
    }

    // ---------------------------------------------------------------- arayuz yenileme

    double WindowSeconds()
    {
        int i = cmbWindow.SelectedIndex;
        return i >= 0 && i < WindowSecs.Length ? WindowSecs[i] : 120;
    }

    void RefreshUi()
    {
        lock (logQueue)
            while (logQueue.Count > 0)
            {
                lstLog.Items.Insert(0, logQueue.Dequeue());
                if (lstLog.Items.Count > 1000) lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
            }
        if (!running && uiRunning) StopMeasure(); // is parcacigi kendi durduysa

        lblDevice.Text = deviceText;
        lblDevice.ForeColor = connected ? Ui.Th.Good : Ui.Th.Bad;

        long n, viol; double tNow, rate = 0;
        lock (lk)
        {
            n = samples; viol = violations; tNow = lastT;
            for (int i = 0; i < series.Count && cardValue != null && i < cardValue.Length; i++)
            {
                SeriesData s = series[i];
                cardValue[i].Text = Eng(s.Last, s.P.Unit);
                cardStat[i].Text = s.N == 0 ? "" :
                    "min " + Eng(s.Min, s.P.Unit) + Ui.S("   maks ", "   max ") + Eng(s.Max, s.P.Unit) + Ui.S("\nortalama ", "\navg ") + Eng(s.Sum / s.N, s.P.Unit);
                bool alarm = cfg != null && i == cfg.AlarmIdx && alarmOut && running;
                cardPanel[i].BackColor = alarm ? Ui.Th.CardAlarm : Ui.Th.Card;
            }
            if (series.Count > 0)
            {
                List<double> T = series[0].T;
                int m = Math.Min(T.Count, 20);
                if (m >= 2 && T[T.Count - 1] > T[T.Count - m]) rate = (m - 1) / (T[T.Count - 1] - T[T.Count - m]);
            }
        }
        stState.Text = running ? (connected ? Ui.S("Ölçülüyor", "Measuring") : Ui.S("Cihaz bekleniyor", "Waiting for device")) : Ui.S("Hazır", "Ready");
        stCount.Text = n > 0 ? n + Ui.S(" okuma", " readings") : "";
        stRate.Text = running && rate > 0 ? rate.ToString("F1", Cur) + Ui.S(" okuma/sn", " readings/s") : "";
        stTime.Text = running ? Ui.S("Süre ", "Elapsed ") + Span((DateTime.Now - startedAt).TotalSeconds) : "";
        stAlarm.Text = cfg != null && cfg.AlarmIdx >= 0 && n > 0 ? Ui.S("Limit dışı: ", "Out of limit: ") + viol + Ui.S(" kez", " times") : "";
        stAlarm.ForeColor = viol > 0 ? Ui.Th.Bad : Ui.Th.Text;
        stFile.Text = recFile.Length > 0 ? Ui.S("Kayıt: kayitlar\\", "Recording: kayitlar\\") + recFile : "";
        RefreshChart(tNow);
    }

    void RefreshChart(double tNow)
    {
        ParamInfo p = (ParamInfo)cmbChart.SelectedItem;
        double win = WindowSeconds();
        // Zaman ekseni birimi pencereye gore: saniye / dakika / saat
        double div = win <= 120 ? 1 : win <= 7200 ? 60 : 3600;
        string xUnit = div == 1 ? "s" : div == 60 ? Ui.S("dk", "min") : Ui.S("saat", "h");
        List<string> names = new List<string>();
        List<double[]> xs = new List<double[]>(), ys = new List<double[]>();
        double maxAbs = 0, t0, t1;
        lock (lk)
        {
            // Kaydirma: veri pencereden uzunsa cubuk gecmiste gezdirir; en sagda ise canli veriyi izler
            double tFirst = series.Count > 0 && series[0].T.Count > 0 ? series[0].T[0] : 0;
            double total = tNow - tFirst;
            if (total <= win)
            {
                follow = true;
                scroll.Enabled = false;
                t0 = tFirst; t1 = tFirst + win;
            }
            else
            {
                int large = (int)Math.Max(1, win);
                int max = (int)Math.Ceiling(total);
                int top = Math.Max(0, max - large + 1); // cubugun alabilecegi en buyuk deger
                scroll.Enabled = true;
                if (scroll.Value > top) scroll.Value = top;
                scroll.Maximum = max; scroll.LargeChange = large; scroll.SmallChange = Math.Max(1, large / 10);
                if (follow) scroll.Value = top;
                t0 = follow ? tNow - win : tFirst + scroll.Value;
                t1 = t0 + win;
            }
            lblHistory.Visible = !follow;
            foreach (SeriesData s in series)
            {
                if (s.P != p) continue;
                int i0 = s.T.BinarySearch(t0), i1 = s.T.BinarySearch(t1);
                if (i0 < 0) i0 = ~i0;
                if (i1 < 0) i1 = ~i1; else i1++;
                List<double> x = new List<double>(), y = new List<double>();
                Decimate(s.T, s.V, i0, i1, x, y);
                for (int j = 0; j < x.Count; j++) x[j] /= div;
                foreach (double v in y) if (Math.Abs(v) > maxAbs) maxAbs = Math.Abs(v);
                names.Add(s.Ch); xs.Add(x.ToArray()); ys.Add(y.ToArray());
            }
        }
        // eksen birimi: degerler kucukse mV / µs gibi olcekle
        string pre = ""; double k = 1;
        if (p.Unit != "%" && maxAbs > 0)
        {
            if (maxAbs >= 1e6) { pre = "M"; k = 1e-6; }
            else if (maxAbs >= 1e3) { pre = "k"; k = 1e-3; }
            else if (maxAbs >= 1) { }
            else if (maxAbs >= 1e-3) { pre = "m"; k = 1e3; }
            else if (maxAbs >= 1e-6) { pre = "µ"; k = 1e6; }
            else { pre = "n"; k = 1e9; }
        }
        ChartArea area = chart.ChartAreas[0];
        area.AxisY.Title = p.Code + " – " + p.Name + " [" + pre + p.Unit + "]";
        area.AxisX.Title = Ui.S("Süre [", "Time [") + xUnit + "]";
        area.AxisX.Minimum = t0 / div;
        area.AxisX.Maximum = t1 / div;

        if (chart.Series.Count != names.Count) chart.Series.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            if (chart.Series.Count <= i)
            {
                Series cs = new Series();
                cs.ChartType = SeriesChartType.Line; cs.BorderWidth = 2;
                chart.Series.Add(cs);
            }
            Series c = chart.Series[i];
            c.LegendText = names[i];
            c.Color = Ui.Th.Chan[Array.IndexOf(Chans, names[i])];
            double[] y = ys[i];
            for (int j = 0; j < y.Length; j++) y[j] *= k;
            c.Points.DataBindXY(xs[i], y);
        }

        area.AxisY.StripLines.Clear();
        if (cfg != null && cfg.AlarmIdx >= 0 && cfg.AlarmIdx < series.Count && series[cfg.AlarmIdx].P == p)
            foreach (double lim in new double[] { cfg.Low, cfg.High })
            {
                if (double.IsNaN(lim)) continue;
                StripLine sl = new StripLine();
                sl.IntervalOffset = lim * k; sl.StripWidth = 0;
                sl.BorderColor = Ui.Th.Bad; sl.BorderWidth = 1; sl.BorderDashStyle = ChartDashStyle.Dash;
                area.AxisY.StripLines.Add(sl);
            }
        area.RecalculateAxesScale();
    }

    // Cok nokta varsa her dilimin en dusuk ve en yuksek degerini birakir; kisa sureli kopmalar grafikte kaybolmaz
    static void Decimate(List<double> T, List<double> V, int i0, int i1, List<double> x, List<double> y)
    {
        const int MaxBuckets = 1500;
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
            x.Add(T[a]); y.Add(V[a]);
            if (b != a) { x.Add(T[b]); y.Add(V[b]); }
        }
    }

    // ---------------------------------------------------------------- ayarlar

    void SaveSettings()
    {
        try
        {
            List<string> ch = new List<string>(), pr = new List<string>();
            for (int i = 0; i < 4; i++) if (chkChan[i].Checked) ch.Add(Chans[i]);
            foreach (object o in lstParams.CheckedItems) pr.Add(((ParamInfo)o).Code);
            File.WriteAllLines(IniPath, new string[] {
                "kanallar=" + string.Join(",", ch.ToArray()),
                "parametreler=" + string.Join(",", pr.ToArray()),
                "aralik=" + numInterval.Value.ToString(Inv),
                "kayit=" + (chkRecord.Checked ? "1" : "0"),
                "grafik=" + ((ParamInfo)cmbChart.SelectedItem).Code,
                "pencere=" + cmbWindow.SelectedIndex,
                "limit=" + (chkAlarm.Checked ? "1" : "0"),
                "limit_kanal=" + cmbAlarmCh.SelectedItem,
                "limit_parametre=" + ((ParamInfo)cmbAlarmParam.SelectedItem).Code,
                "limit_alt=" + txtLow.Text,
                "limit_ust=" + txtHigh.Text,
                "ses=" + (chkBeep.Checked ? "1" : "0"),
                "baglanti=" + (cmbConn.SelectedIndex == 1 ? "ag" : "usb"),
                "adres=" + txtAddr.Text.Trim(),
                "komut_seti=" + cmbDialect.SelectedIndex,
                "dil=" + (Ui.En ? "en" : "tr"),
                "tema=" + (Ui.Th.Dark ? "koyu" : "acik"),
            });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void LoadSettings()
    {
        if (!File.Exists(IniPath)) return;
        Dictionary<string, string> d = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(IniPath))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        string v; int n;
        if (d.TryGetValue("kanallar", out v))
        {
            List<string> on = new List<string>(v.Split(','));
            for (int i = 0; i < 4; i++) chkChan[i].Checked = on.Contains(Chans[i]);
        }
        if (d.TryGetValue("parametreler", out v))
        {
            List<string> on = new List<string>(v.Split(','));
            for (int i = 0; i < ParamInfo.All.Length; i++) lstParams.SetItemChecked(i, on.Contains(ParamInfo.All[i].Code));
        }
        if (d.TryGetValue("aralik", out v) && int.TryParse(v, out n)) numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, n));
        if (d.TryGetValue("kayit", out v)) chkRecord.Checked = v == "1";
        if (d.TryGetValue("grafik", out v) && ParamInfo.Find(v) != null) cmbChart.SelectedItem = ParamInfo.Find(v);
        if (d.TryGetValue("pencere", out v) && int.TryParse(v, out n) && n >= 0 && n < cmbWindow.Items.Count) cmbWindow.SelectedIndex = n;
        if (d.TryGetValue("limit", out v)) chkAlarm.Checked = v == "1";
        if (d.TryGetValue("limit_kanal", out v) && Array.IndexOf(Chans, v) >= 0) cmbAlarmCh.SelectedItem = v;
        if (d.TryGetValue("limit_parametre", out v) && ParamInfo.Find(v) != null) cmbAlarmParam.SelectedItem = ParamInfo.Find(v);
        if (d.TryGetValue("limit_alt", out v)) txtLow.Text = v;
        if (d.TryGetValue("limit_ust", out v)) txtHigh.Text = v;
        if (d.TryGetValue("ses", out v)) chkBeep.Checked = v == "1";
        if (d.TryGetValue("adres", out v)) txtAddr.Text = v;
        if (d.TryGetValue("baglanti", out v)) cmbConn.SelectedIndex = v == "ag" ? 1 : 0;
        if (d.TryGetValue("komut_seti", out v) && int.TryParse(v, out n) && n >= 0 && n < cmbDialect.Items.Count) cmbDialect.SelectedIndex = n;
    }

    // --selftest <png> [saniye] [toggle]: olcumu baslatir, bir sure sonra pencerenin goruntusunu kaydedip kapanir (gelistirme icin).
    // "toggle": once dil ve tema calisirken degistirilir. "scroll": goruntuden once grafik gecmisin basina kaydirilir.
    public void SelfTest(string png, int seconds, string mode)
    {
        Shown += delegate
        {
            if (mode == "toggle") { cmbLang.SelectedIndex = 1 - cmbLang.SelectedIndex; Application.DoEvents(); cmbTheme.SelectedIndex = 1 - cmbTheme.SelectedIndex; Application.DoEvents(); }
            StartMeasure();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate
            {
                t.Stop();
                if (mode == "scroll") scroll.Value = 0; // gecmisin basina kaydir
                RefreshUi();
                // Ekrandaki gercek pikseller (yalnizca bu pencerenin alani); DrawToBitmap ozel cizimleri yanlis gosteriyor
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

static class GuiProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm();
        if (args.Length >= 2 && args[0] == "--selftest") f.SelfTest(args[1], args.Length > 2 ? int.Parse(args[2]) : 6, args.Length > 3 ? args[3] : "");
        Application.Run(f);
    }
}
