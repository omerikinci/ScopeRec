// ScopeRec.exe - pencereli olcum uygulamasi. Derleme icin: derle.bat
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

partial class MainForm : Form
{
    static readonly CultureInfo Cur = CultureInfo.CurrentCulture;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string Sep = Cur.TextInfo.ListSeparator;
    static readonly string[] Chans = { "C1", "C2", "C3", "C4" };
    // Sorgular arka arkaya gelirse SDS1104X-E yeni yakalama yapamiyor (ekran donuyor, deger yenilenmiyor); olculen esik ~30 ms
    const int MinInterval = 40;

    readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    // Kayitlarin yazildigi yer: secilen konum + klasor adi (varsayilan: uygulamanin yanindaki "kayitlar")
    string recBase = DefaultBase, recName = "kayitlar";
    static string DefaultBase { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); } }
    string RecDir { get { return Path.Combine(recBase, recName); } }
    string IniPath { get { return Path.Combine(baseDir, "ayarlar.ini"); } }

    // arayuz
    Button btnStart, btnEnd, btnFolder, btnShot, btnReset, btnDefaults;
    int logHeight = 150;        // olay listesinin yuksekligi (suruklenerek degisir)
    bool testActive;            // bir test acik (olcuyor ya da duraklatilmis); "Testi bitir" ile kapanir
    volatile string testStem;   // acik testin kayit dosyalarinin kok adi (kayit yoksa null)
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
    Label lblHover;
    Point hoverPt;
    bool hovering;
    double chartDiv = 1; // zaman ekseninin birimi (saniye / dakika / saat) kac saniye
    double zoomWin;                                  // tekerlekle secilen zaman araligi (sn); 0 = listedeki secim
    double yLo = double.NaN, yHi = double.NaN;       // tekerlekle secilen deger araligi (temel birim); NaN = otomatik
    double pendingStart = double.NaN;                // yakinlastirmadan sonra gorunumun baslayacagi an
    double viewT0, viewT1;                           // grafikte su an gosterilen zaman araligi (sn)
    double lastK = 1;                                // deger ekseninin olcegi (ornegin mV icin 1000)
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
        Text = "ScopeRec";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(Math.Min(1560, Screen.PrimaryScreen.WorkingArea.Width - 60), Math.Min(820, Screen.PrimaryScreen.WorkingArea.Height - 80));
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
        FormClosing += delegate(object s, FormClosingEventArgs e) { OnClosingForm(e); };
        Shown += delegate { Config c = ReadConn(); ThreadPool.QueueUserWorkItem(delegate { if (!running) WithDevice(c, null); }); };
        Shown += delegate { SerialAutoConnect(); }; // gecen sefer acik olan seri baglantilari yeniden ac
    }

    // ---------------------------------------------------------------- arayuz kurulumu

    void BuildUi()
    {
        TableLayoutPanel main = new TableLayoutPanel();
        main.Dock = DockStyle.Fill;
        main.ColumnCount = 1; main.RowCount = 4;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        main.Padding = new Padding(4);

        cards = new FlowLayoutPanel();
        cards.Dock = DockStyle.Fill; cards.AutoScroll = true;
        main.Controls.Add(cards, 0, 0);

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Fill; bar.WrapContents = false; // dar pencerede alt satira kayip gorunmez olmasin
        bar.Controls.Add(MakeLabel(Ui.S("Grafik:", "Chart:"), 6));
        cmbChart = MakeCombo(190);
        foreach (ParamInfo p in ParamInfo.All) cmbChart.Items.Add(p);
        cmbChart.SelectedIndex = 0;
        cmbChart.SelectedIndexChanged += delegate { yLo = yHi = double.NaN; }; // baska parametrenin deger araligi anlamsiz
        bar.Controls.Add(cmbChart);
        bar.Controls.Add(MakeLabel(Ui.S("Zaman aralığı:", "Time span:"), 6));
        cmbWindow = MakeCombo(100);
        cmbWindow.Items.AddRange(new object[] { Ui.S("30 sn", "30 s"), Ui.S("2 dk", "2 min"), Ui.S("10 dk", "10 min"), Ui.S("30 dk", "30 min"), Ui.S("1 saat", "1 hour"),
            Ui.S("6 saat", "6 hours"), Ui.S("12 saat", "12 hours"), Ui.S("1 gün", "1 day"), Ui.S("3 gün", "3 days") });
        cmbWindow.SelectedIndex = 1;
        cmbWindow.SelectedIndexChanged += delegate { zoomWin = 0; };
        bar.Controls.Add(cmbWindow);
        Button btnClear = new Button();
        btnClear.Text = Ui.S("Grafiği temizle", "Clear chart"); btnClear.Size = new Size(120, 25); btnClear.Margin = new Padding(12, 2, 0, 0);
        btnClear.Click += delegate { ClearChart(); };
        bar.Controls.Add(btnClear);
        Button btnImage = new Button();
        btnImage.Text = Ui.S("Resim kaydet", "Save image"); btnImage.Size = new Size(104, 25); btnImage.Margin = new Padding(6, 2, 0, 0);
        btnImage.Click += delegate { SaveChartImage(); };
        bar.Controls.Add(btnImage);
        lblHistory = MakeLabel(Ui.S("Geçmiş gösteriliyor – canlı için çubuğu sağa çekin", "Viewing history – drag the bar right for live"), 6);
        lblHistory.Margin = new Padding(16, 6, 0, 0);
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
        lblHover = HoverTip.Create(chart);
        hovering = false;
        chart.MouseMove += delegate(object s, MouseEventArgs e) { hoverPt = e.Location; hovering = true; UpdateHover(); };
        chart.MouseLeave += delegate { hovering = false; lblHover.Visible = false; };
        chart.MouseEnter += delegate { FocusChart(); };
        chart.MouseWheel += delegate(object s, MouseEventArgs e) { ChartWheel(e); };
        chart.MouseDoubleClick += delegate { ResetZoom(); };
        main.Controls.Add(chart, 0, 2);

        scroll = new HScrollBar();
        scroll.Dock = DockStyle.Fill; scroll.Enabled = false;
        scroll.ValueChanged += delegate { follow = scroll.Value >= scroll.Maximum - scroll.LargeChange + 1; };
        main.Controls.Add(scroll, 0, 3);

        lstLog = new ListBox();
        lstLog.Dock = DockStyle.Fill; lstLog.IntegralHeight = false; lstLog.HorizontalScrollbar = true;
        // Olay listesi grafigin altinda ayri bir bolmede; aradaki cubukla yuksekligi degisir
        Panel logHost = new Panel();
        logHost.Dock = DockStyle.Bottom; logHost.Height = logHeight; logHost.Padding = new Padding(4, 0, 4, 4);
        logHost.Controls.Add(lstLog);          // Dock: son eklenen once yerlesir
        logHost.Controls.Add(BuildTestRow());
        GripSplitter logSplit = new GripSplitter(DockStyle.Bottom);
        logSplit.MinSize = 60; logSplit.MinExtra = 220;
        logSplit.SplitterMoved += delegate { logHeight = logHost.Height; };
        Panel center = new Panel();
        center.Dock = DockStyle.Fill;
        center.Controls.Add(main);      // Dock: son eklenen once yerlesir
        center.Controls.Add(logSplit);
        center.Controls.Add(logHost);

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

        btnDefaults = new Button();
        btnDefaults.Text = Ui.S("Varsayılan ayarlara dön", "Restore default settings"); btnDefaults.SetBounds(6, y, 234, 28);
        btnDefaults.Click += delegate { ResetDefaults(); };
        left.Controls.Add(btnDefaults);



        // ust cubuk
        Panel top = new Panel();
        top.Dock = DockStyle.Top; top.Height = 58; top.BackColor = Ui.Th.Bar;
        btnStart = new Button();
        btnStart.SetBounds(10, 9, 130, 40);
        btnStart.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        btnStart.FlatStyle = FlatStyle.Flat; btnStart.ForeColor = Color.White;
        btnStart.Click += delegate { if (running) StopMeasure(); else StartMeasure(); };
        chkRecord = new CheckBox(); chkRecord.Text = Ui.S("CSV dosyasına kaydet", "Record to CSV file"); chkRecord.Checked = true;
        btnEnd = new Button();
        btnEnd.Text = Ui.S("■  Testi bitir", "■  End test"); btnEnd.SetBounds(146, 9, 104, 40);
        btnEnd.Click += delegate { EndTest(true); };
        chkRecord.SetBounds(262, 19, 160, 22);
        chkRecord.CheckedChanged += delegate { wantRecord = chkRecord.Checked; };
        btnFolder = MakeButton(Ui.S("Kayıt yeri…", "Record to…"), 426, delegate { RecordSettings(); });
        btnShot = MakeButton(Ui.S("Ekran görüntüsü al", "Take screenshot"), 542, delegate { TakeShot(); });
        btnShot.Width = 130;
        btnReset = MakeButton(Ui.S("İstatistiği sıfırla", "Reset statistics"), 678, delegate { lock (lk) { foreach (SeriesData s in series) s.ResetStats(); violations = 0; } });
        btnReset.Width = 120;
        lblDevice = new Label();
        lblDevice.Dock = DockStyle.Right; lblDevice.Width = 270;
        lblDevice.Padding = new Padding(0, 0, 10, 0);
        lblDevice.TextAlign = ContentAlignment.MiddleRight;
        // dil ve tema: degisince arayuz bastan kurulur (olcum surerken kilitli)
        cmbLang = MakeCombo(78); cmbLang.Items.AddRange(new object[] { "Türkçe", "English" });
        cmbLang.SelectedIndex = Ui.En ? 1 : 0;
        cmbLang.SetBounds(810, 17, 78, 24);
        cmbLang.SelectedIndexChanged += delegate { Ui.En = cmbLang.SelectedIndex == 1; BeginInvoke(new MethodInvoker(Rebuild)); };
        cmbTheme = MakeCombo(74); cmbTheme.Items.AddRange(new object[] { Ui.S("Açık", "Light"), Ui.S("Koyu", "Dark") });
        cmbTheme.SelectedIndex = Ui.Th.Dark ? 1 : 0;
        cmbTheme.SetBounds(894, 17, 74, 24);
        cmbTheme.SelectedIndexChanged += delegate { Ui.Th = cmbTheme.SelectedIndex == 1 ? Theme.MakeDark() : Theme.Light(); BeginInvoke(new MethodInvoker(Rebuild)); };
        top.Controls.AddRange(new Control[] { btnStart, btnEnd, chkRecord, btnFolder, btnShot, btnReset, cmbLang, cmbTheme, lblDevice });
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
        Controls.Add(center);
        BuildSerialPanel();
        Controls.Add(left);
        Controls.Add(top);
        Controls.Add(st);
        wantRecord = true;
        SetStartButton();
        // Etiketler acik kalir (koyu temada devre disi yazi okunmuyor); yalnizca girdi denetimleri kilitlenir
        List<Control> locks = new List<Control>();
        foreach (GroupBox g in new GroupBox[] { gConn, gCh, gPar, gInt, gAl })
            foreach (Control c in g.Controls) if (!(c is Label)) locks.Add(c);
        locks.Add(cmbLang); locks.Add(cmbTheme); locks.Add(btnDefaults);
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
        SerialTheme();
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
            if (c is TextBox || c is ListBox || c is NumericUpDown || c is ComboBox || c is ListView)
            {
                c.BackColor = t.Input; c.ForeColor = t.Text;
                if (t.Dark)
                {
                    if (c is ComboBox)
                    {
                        // gorsel stil arka plan rengini yok saydigi icin duz stil + kendi cizimimiz
                        ComboBox cb = (ComboBox)c;
                        cb.FlatStyle = FlatStyle.Flat;
                        // yazilabilir listede kendi cizim kipi yazi kutusunu beyaz birakiyor; yalnizca secmeli listelerde kullanilir
                        if (cb.DropDownStyle == ComboBoxStyle.DropDownList) { cb.DrawMode = DrawMode.OwnerDrawFixed; cb.DrawItem += DrawComboItem; }
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
    // Kayit yeri penceresi: kayitlarin yazilacagi konum ve klasor adi. Olcum surerken yalnizca klasor acilabilir.
    void RecordSettings()
    {
        using (Form f = new Form())
        {
            f.Text = Ui.S("Kayıt yeri", "Recording location");
            f.Font = Font; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent; f.ClientSize = new Size(520, 196);
            f.BackColor = Ui.Th.Back; f.ForeColor = Ui.Th.Text;

            Label l1 = new Label(); l1.Text = Ui.S("Konum:", "Location:"); l1.SetBounds(14, 20, 90, 20);
            TextBox txtBase = new TextBox(); txtBase.Text = recBase; txtBase.SetBounds(108, 17, 300, 24);
            Button btnBrowse = new Button(); btnBrowse.Text = Ui.S("Gözat…", "Browse…"); btnBrowse.SetBounds(416, 15, 90, 28);
            Label l2 = new Label(); l2.Text = Ui.S("Klasör adı:", "Folder name:"); l2.SetBounds(14, 56, 90, 20);
            TextBox txtName = new TextBox(); txtName.Text = recName; txtName.SetBounds(108, 53, 300, 24);
            // Salt okunur metin kutusu: uzun yollar bosluk olmasa da alt satira kayar
            TextBox lFull = new TextBox(); lFull.ReadOnly = true; lFull.Multiline = true; lFull.TabStop = false; lFull.SetBounds(14, 88, 492, 52);
            Button btnOpenDir = new Button(); btnOpenDir.Text = Ui.S("Klasörü aç", "Open folder"); btnOpenDir.SetBounds(14, 150, 110, 30);
            Button ok = new Button(); ok.Text = Ui.S("Tamam", "OK"); ok.SetBounds(300, 150, 100, 30);
            Button cancel = new Button(); cancel.Text = Ui.S("İptal", "Cancel"); cancel.SetBounds(406, 150, 100, 30); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { l1, txtBase, btnBrowse, l2, txtName, lFull, btnOpenDir, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            PaintInputs(f);
            lFull.BorderStyle = BorderStyle.None; lFull.BackColor = Ui.Th.Back; lFull.ForeColor = Ui.Th.Muted;
            txtBase.Enabled = txtName.Enabled = btnBrowse.Enabled = ok.Enabled = !running;

            EventHandler preview = delegate
            {
                string p;
                try { p = Path.Combine(txtBase.Text.Trim(), txtName.Text.Trim()); } catch (ArgumentException) { p = ""; }
                lFull.Text = Ui.S("Kayıtlar buraya yazılır:", "Recordings are written to:") + "\r\n" + p;
            };
            txtBase.TextChanged += preview; txtName.TextChanged += preview;
            preview(null, null);
            btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = Ui.S("Kayıt klasörünün oluşturulacağı konumu seçin", "Choose where the recording folder will be created");
                    if (Directory.Exists(txtBase.Text.Trim())) d.SelectedPath = txtBase.Text.Trim();
                    if (d.ShowDialog(f) == DialogResult.OK) txtBase.Text = d.SelectedPath;
                }
            };
            btnOpenDir.Click += delegate
            {
                try { Directory.CreateDirectory(RecDir); Process.Start(RecDir); }
                catch (Exception e) { MessageBox.Show(f, e.Message, f.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            ok.Click += delegate
            {
                string b = txtBase.Text.Trim(), n = txtName.Text.Trim();
                string problem = null;
                if (b.Length == 0 || n.Length == 0) problem = Ui.S("Konum ve klasör adı boş olamaz.", "Location and folder name cannot be empty.");
                else if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problem = Ui.S("Klasör adında kullanılamayan bir karakter var (/ : * ? < > | gibi).", "The folder name contains a character that is not allowed (such as / : * ? < > |).");
                else
                {
                    // Yazilabildigini simdi dene: hata test basladiktan sonra degil burada gorulsun
                    try { Directory.CreateDirectory(Path.Combine(b, n)); }
                    catch (Exception e) { problem = Ui.S("Klasör oluşturulamadı: ", "Could not create the folder: ") + e.Message; }
                }
                if (problem != null) { MessageBox.Show(f, problem, f.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                recBase = b; recName = n;
                SaveSettings();
                f.DialogResult = DialogResult.OK;
            };
            f.ShowDialog(this);
        }
    }

    // ---------------------------------------------------------------- test araclari: ad, isaret, ozet, resim, goruntuleyici

    TextBox txtTestName, txtMark;
    string testName = "";                                   // kayit dosyalarinin basina eklenir
    string lastStem;                                        // en son kaydin kok adi (ScopeView dugmesi icin)
    readonly List<double> marks = new List<double>();       // isaret zamanlari (grafikte dikey cizgi; kilit: lk)
    bool awake;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    // Olay listesinin ustundeki satir: test adi, isaret notu, ScopeView
    Panel BuildTestRow()
    {
        Panel row = new Panel();
        row.Dock = DockStyle.Top; row.Height = 32;
        Label l1 = new Label(); l1.Text = Ui.S("Test adı:", "Test name:"); l1.SetBounds(0, 8, 66, 18);
        txtTestName = new TextBox(); txtTestName.Text = testName; txtTestName.SetBounds(68, 4, 150, 24);
        txtTestName.TextChanged += delegate { testName = txtTestName.Text.Trim(); };
        Label l2 = new Label(); l2.Text = Ui.S("İşaret:", "Mark:"); l2.SetBounds(230, 8, 44, 18);
        txtMark = new TextBox(); txtMark.SetBounds(276, 4, 220, 24);
        txtMark.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddMark(); } };
        Button btnMark = new Button(); btnMark.Text = Ui.S("İşaret koy", "Add mark"); btnMark.SetBounds(502, 2, 90, 28);
        btnMark.Click += delegate { AddMark(); };
        Button btnView = new Button(); btnView.Text = Ui.S("ScopeView ile aç", "Open in ScopeView"); btnView.SetBounds(604, 2, 130, 28);
        btnView.Click += delegate { OpenInViewer(); };
        row.Controls.AddRange(new Control[] { l1, txtTestName, l2, txtMark, btnMark, btnView });
        return row;
    }

    // Test adindan dosya adinda kullanilabilecek on ek: "TEST5 15A" -> "TEST5 15A_"; bossa "olcum_"
    string FilePrefix()
    {
        string n = testName;
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '-');
        n = n.Trim().TrimEnd('.');
        return n.Length > 0 ? n + "_" : "olcum_";
    }

    // Su anki ana isaret koyar: olay listesine (ve kayit aciksa log dosyasina) notuyla yazilir, grafikte dikey cizgi olur.
    // Test sirasinda "yuk baglandi", "akim 15A yapildi" gibi anlari sonradan bulmak icin.
    void AddMark()
    {
        string note = txtMark.Text.Trim();
        double t = plotClock.Elapsed.TotalSeconds;
        lock (lk) { marks.Add(t); if (marks.Count > 2000) marks.RemoveAt(0); }
        Log(Ui.S("İŞARET", "MARK") + (note.Length > 0 ? ": " + note : "") + "  (t = " + Span(t) + ")");
        txtMark.SelectAll();
    }

    // Test bitince olay listesine ve log dosyasina yazilan ozet
    void LogSummary()
    {
        List<string> lines = new List<string>();
        lock (lk)
        {
            lines.Add(Ui.S("Özet – süre ", "Summary – duration ") + Span((DateTime.Now - startedAt).TotalSeconds) + ", " + samples + Ui.S(" okuma", " readings")
                + (cfg != null && cfg.AlarmIdx >= 0 ? ", " + Ui.S("limit dışı ", "out of limit ") + violations + Ui.S(" kez", " times") : ""));
            foreach (SeriesData s in series)
                if (s.N > 0)
                    lines.Add("   " + s.Ch + " " + s.P.Code + ":  min " + Eng(s.Min, s.P.Unit) + Ui.S("   maks ", "   max ") + Eng(s.Max, s.P.Unit)
                        + Ui.S("   ortalama ", "   mean ") + Eng(s.Sum / s.N, s.P.Unit));
            int shown = 0;
            foreach (string key in serKeys)
            {
                SeriesData s = serSeries[key];
                if (s.N == 0 || ++shown > 12) continue;
                lines.Add("   " + key + Ui.S(" (seri)", " (serial)") + ":  min " + SerNum(s.Min) + Ui.S("   maks ", "   max ") + SerNum(s.Max)
                    + Ui.S("   ortalama ", "   mean ") + SerNum(s.Sum / s.N));
            }
        }
        foreach (string l in lines) Log(l);
    }

    void SaveChartImage()
    {
        try
        {
            Directory.CreateDirectory(RecDir);
            string path = Path.Combine(RecDir, FilePrefix() + Ui.S("grafik_", "chart_") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            chart.SaveImage(path, ChartImageFormat.Png);
            Log(Ui.S("Grafik resmi kaydedildi: ", "Chart image saved: ") + path);
        }
        catch (Exception e) { Log(Ui.S("Grafik resmi kaydedilemedi: ", "Could not save the chart image: ") + e.Message); }
    }

    // En son (ya da suren) kaydi ScopeView ile acar; kayit yoksa ScopeView bos acilir
    void OpenInViewer()
    {
        string exe = Path.Combine(baseDir, "ScopeView.exe");
        if (!File.Exists(exe)) { Log(Ui.S("ScopeView.exe bu klasörde bulunamadı", "ScopeView.exe was not found in this folder")); return; }
        string stem = testStem ?? lastStem;
        try
        {
            if (stem != null && File.Exists(stem + ".csv")) Process.Start(exe, "\"" + stem + ".csv\"");
            else Process.Start(exe);
        }
        catch (Exception e) { Log(Ui.S("ScopeView açılamadı: ", "Could not open ScopeView: ") + e.Message); }
    }

    // Test surerken ya da seri port acikken bilgisayar kendiliginden uykuya gecmesin (ekran kapanabilir).
    // Arayuz is parcacigindan cagrilmali: ayar is parcacigina baglidir.
    void KeepAwake()
    {
        bool want = testActive;
        foreach (SerSession s in ses) if (s.Link != null) want = true;
        if (want == awake) return;
        awake = want;
        try { SetThreadExecutionState(want ? 0x80000001u : 0x80000000u); } catch (Exception) { } // ES_CONTINUOUS [+ ES_SYSTEM_REQUIRED]
    }

    // Kapatma onayi: suren test yanlislikla kapatilmasin
    void OnClosingForm(FormClosingEventArgs e)
    {
        if (testActive && e.CloseReason == CloseReason.UserClosing &&
            !Confirm(Ui.S("Bir test sürüyor. Pencere kapatılırsa test bitirilir ve kayıt dosyaları kapatılır.\n\nKapatılsın mı?",
                          "A test is in progress. Closing the window ends the test and closes the record files.\n\nClose anyway?")))
        { e.Cancel = true; return; }
        EndTest(false); SaveSettings(); SerialDisconnectAll();
        try { SetThreadExecutionState(0x80000000u); } catch (Exception) { }
    }

    bool testNoAsk; // --selftest: onay pencereleri atlanir

    // Evet / Hayir onayi; varsayilan dugme Hayir
    bool Confirm(string question)
    {
        return testNoAsk || MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    // Grafikte ve bellekte biriken olcum gecmisini siler; CSV kaydina dokunmaz, olcum suruyorsa devam eder
    void ClearChart()
    {
        if (!Confirm(Ui.S("Tüm grafik silinecektir. Devam edilsin mi?\n\n(CSV dosyasına yazılmış kayıtlar silinmez.)",
                          "The whole chart will be cleared. Continue?\n\n(Recordings already written to CSV are not deleted.)"))) return;
        lock (lk) { foreach (SeriesData s in series) { s.T.Clear(); s.V.Clear(); s.ResetStats(); } }
        ClearSerialSeries();
        lock (lk) marks.Clear();
        follow = true;
    }

    // Tum ayarlari ilk kurulumdaki haline getirir (dil ve tema ayni kalir)
    void ResetDefaults()
    {
        if (testActive) return;
        if (!Confirm(Ui.S("Tüm ayarlar varsayılan değerlere dönecek (kanal, parametre, limitler, bağlantı). Devam edilsin mi?",
                          "All settings will return to their defaults (channels, parameters, limits, connection). Continue?"))) return;
        recBase = DefaultBase; recName = "kayitlar";
        SerialDefaults();
        try { File.Delete(IniPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Rebuild(false);
        SaveSettings();
    }

    void Rebuild() { Rebuild(true); }

    void Rebuild(bool keepSettings)
    {
        if (testActive) return;
        if (keepSettings) SaveSettings();
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
    readonly Dictionary<string, string> serIni = new Dictionary<string, string>();

    void LoadPrefs()
    {
        Ui.En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr";
        if (!File.Exists(IniPath)) return;
        foreach (string line in File.ReadAllLines(IniPath))
        {
            if (line == "dil=en") Ui.En = true;
            else if (line == "dil=tr") Ui.En = false;
            else if (line == "tema=koyu") Ui.Th = Theme.MakeDark();
            else if (line.StartsWith("test_adi=")) testName = line.Substring(9);
            else if (line.StartsWith("olay_yukseklik=")) { int lh; if (int.TryParse(line.Substring(15), out lh)) logHeight = Math.Max(60, Math.Min(600, lh)); }
            else if (line.StartsWith("seri") && line.IndexOf('=') > 0) serIni[line.Substring(0, line.IndexOf('='))] = line.Substring(line.IndexOf('=') + 1);
        }
        SerialLoadSettings(serIni);
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
        btnStart.Text = running ? Ui.S("‖  Duraklat", "‖  Pause") : testActive ? Ui.S("▶  Devam et", "▶  Resume") : Ui.S("▶  Başlat", "▶  Start");
        if (btnEnd != null) btnEnd.Enabled = testActive;
        btnStart.BackColor = running ? Color.FromArgb(200, 120, 0) : Color.FromArgb(46, 125, 50);
    }

    void UpdateLimitLabels()
    {
        string unit = ((ParamInfo)cmbAlarmParam.SelectedItem).Unit;
        lblLow.Text = Ui.S("Alt limit [", "Lower limit [") + unit + "]:";
        lblHigh.Text = Ui.S("Üst limit [", "Upper limit [") + unit + "]:";
    }

    // ---------------------------------------------------------------- bicimlendirme

    static string Eng(double v, string unit) { return Fmt.Eng(v, unit); }
    static string Plain(double x, int digits) { return Fmt.Plain(x, digits); }
    static double ParseLimit(string s) { return Fmt.Parse(s); }
    static string Span(double sec) { return Fmt.Span(sec); }

    // Kayit acikken olay listesindeki her satir <kayit>_log.txt dosyasina da yazilir (zaman, t[s], mesaj; sekmeyle ayrik).
    // ScopeView bu dosyayi olcum dosyasiyla birlikte acip olaylari grafikte gosterir.
    StreamWriter logFile;
    Stopwatch runClock;

    void Log(string msg)
    {
        lock (logQueue)
        {
            logQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
            if (logFile != null)
                logFile.WriteLine(DateTime.Now.ToString("G", Cur) + "\t" + runClock.Elapsed.TotalSeconds.ToString("F2", Cur) + "\t" + msg);
        }
    }

    // Kayit bitince dosyalarin tam yerini olay listesine yazar
    void LogSaved(string stem)
    {
        Log(Ui.S("Kayıt dosyası: ", "Recording file: ") + stem + ".csv");
        Log(Ui.S("Log dosyası: ", "Log file: ") + stem + "_log.txt");
        foreach (SerSession s in ses) if (File.Exists(stem + s.FileTag)) Log(Ui.S("Seri port kaydı: ", "Serial record: ") + stem + s.FileTag);
    }

    void SetLogFile(string path)
    {
        lock (logQueue)
        {
            if (logFile != null) { logFile.Close(); logFile = null; }
            if (path == null) return;
            logFile = new StreamWriter(path, false, new UTF8Encoding(true));
            logFile.AutoFlush = true;
        }
    }


    // ---------------------------------------------------------------- baslat / durdur

    void StartMeasure()
    {
        if (testActive) { ResumeMeasure(); return; } // duraklatilmis test: kaldigi yerden devam
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
        follow = true; zoomWin = 0; yLo = yHi = double.NaN; pendingStart = double.NaN;
        lock (lk) { series = list; samples = 0; violations = 0; lastT = 0; marks.Clear(); }
        plotClock = Stopwatch.StartNew(); // olcum ve seri veri ayni sifirdan baslar
        ClearSerialSeries();
        lock (lk) marks.Clear();
        cfg = c;
        alarmOut = false;
        // grafikte secili parametre olculmuyorsa ilk olculene gec
        if (!c.Params.Contains((ParamInfo)cmbChart.SelectedItem)) cmbChart.SelectedItem = c.Params[0];
        BuildCards();
        startedAt = DateTime.Now;
        testActive = true; testStem = null;
        running = true; uiRunning = true;
        wantRecord = chkRecord.Checked;
        foreach (Control ctl in lockWhileRunning) ctl.Enabled = false;
        SetStartButton();
        SaveSettings();
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Start();
    }

    // Duraklat: olcum is parcacigini durdurur, cihazi birakir. Test acik kalir: grafik, kayit dosyalari ve sure korunur,
    // "Devam et" ile ayni teste kaldigi yerden devam edilir.
    void StopMeasure()
    {
        if (!running && !uiRunning) return;
        running = false;
        if (worker != null) worker.Join(6000);
        worker = null; uiRunning = false;
        SetStartButton();
    }

    // Kaldigi yerden devam: seriler, zaman ekseni ve kayit dosyalari ayni; aradaki bosluk grafikte kesik olarak gorunur
    void ResumeMeasure()
    {
        double t = plotClock.Elapsed.TotalSeconds;
        lock (lk) foreach (SeriesData s in series) if (s.T.Count > 0) s.Add(t, double.NaN); // cizgi duraklamanin ustunden gecmesin
        running = true; uiRunning = true;
        wantRecord = chkRecord.Checked;
        SetStartButton();
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Start();
    }

    // Testi bitir: olcumu durdurur, kayit dosyalarini kapatir. Sonraki "Baslat" sifirdan yeni bir test acar.
    void EndTest(bool ask)
    {
        if (!testActive) return;
        if (ask && !Confirm(Ui.S("Test bitirilsin mi?\n\nKayıt dosyaları kapatılır; yeniden başlatınca grafik ve süre sıfırdan başlar.",
                                 "End the test?\n\nThe record files are closed; starting again begins a new test from zero."))) return;
        StopMeasure();
        testActive = false;
        string stem = testStem;
        testStem = null; measStem = null;
        Log(Ui.S("Test bitti", "Test ended"));
        LogSummary();
        lastStem = stem ?? lastStem;
        if (stem != null) LogSaved(stem);
        SetLogFile(null);
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
        string recPath = null; // acik kaydin uzantisiz tam yolu
        Stopwatch sw = plotClock;
        runClock = sw;
        int errs = 0;
        double outSince = 0;
        double[] vals = new double[series.Count];
        Log(samples > 0 ? Ui.S("Ölçüme devam ediliyor", "Measurement resumed") : Ui.S("Ölçüm başladı", "Measurement started"));
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
                        // Duraklatilmis testin devami ise ayni dosyalarin sonuna eklenir; yeni testte yeni dosyalar acilir
                        string name = testStem;
                        bool resume = name != null && File.Exists(name + ".csv");
                        if (!resume)
                        {
                            Directory.CreateDirectory(RecDir);
                            name = Path.Combine(RecDir, FilePrefix() + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        }
                        csv = new StreamWriter(name + ".csv", resume, new UTF8Encoding(true));
                        csv.AutoFlush = true;
                        if (!resume)
                        {
                            StringBuilder h = new StringBuilder(Ui.S("zaman", "time") + Sep + "t[s]");
                            foreach (SeriesData s in series) h.Append(Sep + s.Key + "[" + s.P.Unit + "]");
                            csv.WriteLine(h);
                        }
                        if (c.AlarmIdx >= 0)
                        {
                            evCsv = new StreamWriter(name + "_olaylar.csv", resume, new UTF8Encoding(true));
                            evCsv.AutoFlush = true;
                            if (!resume) evCsv.WriteLine(Ui.S("zaman", "time") + Sep + "t[s]" + Sep + Ui.S("olay", "event") + Sep + Ui.S("deger", "value") + Sep + Ui.S("limit disi sure[s]", "out of limit[s]"));
                        }
                        if (!resume) SetLogFile(name + "_log.txt"); // devamda log dosyasi zaten acik
                        recPath = name;
                        testStem = name;
                        measStem = name; // seri port satirlari da ayni ada yazilsin
                        recFile = Path.GetFileName(name + ".csv");
                        Log((resume ? Ui.S("Kayda devam: ", "Recording continues: ") : Ui.S("Kayıt: ", "Recording: ")) + recFile);
                    }
                    else if (!wantRecord && csv != null)
                    {
                        // once olay listesine (ve log dosyasina) nereye yazildigini dus, sonra dosyalari kapat
                        Log(Ui.S("Kayıt durduruldu", "Recording stopped"));
                        LogSaved(recPath); recPath = null;
                        measStem = null; testStem = null; // kayit yeniden acilirsa yeni dosya baslar
                        csv.Close(); csv = null;
                        SetLogFile(null);
                        if (evCsv != null) { evCsv.Close(); evCsv = null; }
                        recFile = "";
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
            // Duraklatma: test acik kalir. Log dosyasi ve seri kayitlar kapanmaz; "Testi bitir" kapatir.
            Log(Ui.S("Ölçüm duraklatıldı", "Measurement paused"));
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
                if (u == null && running) return false; // cihaz olcum is parcaciginda acik; durum yazisini bozma
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
        if (zoomWin > 0) return zoomWin;
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
            n = samples; viol = violations; tNow = Math.Max(lastT, serLastT);
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
        stState.Text = running ? (connected ? Ui.S("Ölçülüyor", "Measuring") : Ui.S("Cihaz bekleniyor", "Waiting for device")) : testActive ? Ui.S("Duraklatıldı", "Paused") : Ui.S("Hazır", "Ready");
        stCount.Text = n > 0 ? n + Ui.S(" okuma", " readings") : "";
        stRate.Text = running && rate > 0 ? rate.ToString("F1", Cur) + Ui.S(" okuma/sn", " readings/s") : "";
        stTime.Text = testActive ? Ui.S("Süre ", "Elapsed ") + Span((DateTime.Now - startedAt).TotalSeconds) : "";
        stAlarm.Text = cfg != null && cfg.AlarmIdx >= 0 && n > 0 ? Ui.S("Limit dışı: ", "Out of limit: ") + viol + Ui.S(" kez", " times") : "";
        stAlarm.ForeColor = viol > 0 ? Ui.Th.Bad : Ui.Th.Text;
        stFile.Text = recFile.Length > 0 ? Ui.S("Kayıt: ", "Recording: ") + recName + "\\" + recFile : "";
        RefreshSerial();
        KeepAwake();
        RefreshChart(tNow);
    }

    void RefreshChart(double tNow)
    {
        ParamInfo p = (ParamInfo)cmbChart.SelectedItem;
        double win = WindowSeconds();
        // Zaman ekseni birimi pencereye gore: saniye / dakika / saat
        double div = win <= 120 ? 1 : win <= 7200 ? 60 : 3600;
        chartDiv = div;
        string xUnit = div == 1 ? "s" : div == 60 ? Ui.S("dk", "min") : Ui.S("saat", "h");
        List<string> names = new List<string>();
        List<double[]> xs = new List<double[]>(), ys = new List<double[]>();
        double maxAbs = 0, t0, t1;
        List<string> serNames = new List<string>();
        List<double[]> serXs = new List<double[]>(), serYs = new List<double[]>();
        lock (lk)
        {
            // Kaydirma: veri pencereden uzunsa cubuk gecmiste gezdirir; en sagda ise canli veriyi izler
            // en eski veri: olcum serileri ve seri port serileri birlikte
            double tFirst = double.MaxValue;
            if (series.Count > 0 && series[0].T.Count > 0) tFirst = series[0].T[0];
            foreach (SeriesData ss in serSeries.Values) if (ss.T.Count > 0 && ss.T[0] < tFirst) tFirst = ss.T[0];
            if (tFirst == double.MaxValue) tFirst = 0;
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
                if (!double.IsNaN(pendingStart))
                {
                    int v = (int)Math.Max(0, Math.Min(Math.Round(pendingStart - tFirst), top));
                    pendingStart = double.NaN;
                    scroll.Value = v;
                    follow = v >= top;
                }
                if (follow) scroll.Value = top;
                t0 = follow ? tNow - win : tFirst + scroll.Value;
                t1 = t0 + win;
            }
            bool zoomed = zoomWin > 0 || !double.IsNaN(yLo);
            lblHistory.Text = !follow ? Ui.S("Geçmiş gösteriliyor – canlı için çubuğu sağa çekin", "Viewing history – drag the bar right for live")
                                      : Ui.S("Yakınlaştırıldı – sıfırlamak için grafiğe çift tıklayın", "Zoomed – double-click the chart to reset");
            lblHistory.Visible = !follow || zoomed;
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
            // seri porttan gelen, isaretli degerler: sag eksende, olceklenmeden
            foreach (string key in serKeys)
            {
                if (!serShown.Contains(key)) continue;
                SeriesData s = serSeries[key];
                int i0 = s.T.BinarySearch(t0), i1 = s.T.BinarySearch(t1);
                if (i0 < 0) i0 = ~i0;
                if (i1 < 0) i1 = ~i1; else i1++;
                List<double> x = new List<double>(), y = new List<double>();
                Decimate(s.T, s.V, i0, i1, x, y);
                for (int j = 0; j < x.Count; j++) x[j] /= div;
                serNames.Add(key); serXs.Add(x.ToArray()); serYs.Add(y.ToArray());
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
        lastK = k;
        area.AxisY.Minimum = double.IsNaN(yLo) ? double.NaN : yLo * k;
        area.AxisY.Maximum = double.IsNaN(yLo) ? double.NaN : yHi * k;
        area.AxisX.Title = Ui.S("Süre [", "Time [") + xUnit + "]";
        viewT0 = t0; viewT1 = t1;
        area.AxisX.Minimum = t0 / div;
        area.AxisX.Maximum = t1 / div;

        if (chart.Series.Count != names.Count + serNames.Count) chart.Series.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            if (chart.Series.Count <= i)
            {
                Series cs = new Series();
                cs.ChartType = SeriesChartType.Line; cs.BorderWidth = 2;
                chart.Series.Add(cs);
            }
            Series c = chart.Series[i];
            c.LegendText = names[i]; c.YAxisType = AxisType.Primary; c.BorderDashStyle = ChartDashStyle.Solid;
            c.Color = Ui.Th.Chan[Array.IndexOf(Chans, names[i])];
            double[] y = ys[i];
            for (int j = 0; j < y.Length; j++) y[j] *= k;
            c.Points.DataBindXY(xs[i], y);
        }

        for (int i = 0; i < serNames.Count; i++)
        {
            int si = names.Count + i;
            if (chart.Series.Count <= si)
            {
                Series cs = new Series();
                cs.ChartType = SeriesChartType.Line; cs.BorderWidth = 2;
                chart.Series.Add(cs);
            }
            Series c = chart.Series[si];
            c.LegendText = serNames[i] + Ui.S(" (seri)", " (serial)"); c.YAxisType = AxisType.Secondary; c.BorderDashStyle = ChartDashStyle.Dash;
            c.Color = SerColors[serKeys.IndexOf(serNames[i]) % SerColors.Length];
            c.Points.DataBindXY(serXs[i], serYs[i]);
        }
        area.AxisY2.Enabled = serNames.Count > 0 ? AxisEnabled.True : AxisEnabled.False;

        // isaretler: gorunen araliktakiler dikey kesikli cizgi
        area.AxisX.StripLines.Clear();
        lock (lk)
            foreach (double mt in marks)
            {
                if (mt < t0 || mt > t1) continue;
                StripLine ml = new StripLine();
                ml.IntervalOffset = mt / div; ml.StripWidth = 0;
                ml.BorderColor = Ui.Th.Chan[2]; ml.BorderWidth = 1; ml.BorderDashStyle = ChartDashStyle.Dash;
                area.AxisX.StripLines.Add(ml);
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
        UpdateHover();
    }

    // Fare tekerlegi: zaman ekseninde yakinlastirir; Ctrl ile birlikte deger ekseninde. Cift tik ikisini de sifirlar.
    void ChartWheel(MouseEventArgs e)
    {
        ChartArea a = chart.ChartAreas[0];
        double k = e.Delta > 0 ? 0.6 : 1 / 0.6;
        try
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                double lo = a.AxisY.Minimum / lastK, hi = a.AxisY.Maximum / lastK;
                if (double.IsNaN(lo) || double.IsNaN(hi) || hi <= lo) return;
                double c = Math.Max(lo, Math.Min(hi, a.AxisY.PixelPositionToValue(e.Y) / lastK));
                yLo = c - (c - lo) * k; yHi = c + (hi - c) * k;
            }
            else
            {
                double x0 = viewT0, x1 = viewT1; // eksenden okumak yerine son cizilen aralik: cizim bitmeden de dogru
                if (double.IsNaN(x0) || double.IsNaN(x1) || x1 <= x0) return;
                double w = Math.Max(2, Math.Min((x1 - x0) * k, WindowSecs[WindowSecs.Length - 1]));
                // Canli izlenirken en yeni veri gorunur kalir; gecmise bakilirken fare altindaki an yerinde kalir
                if (!follow)
                {
                    double c = Math.Max(x0, Math.Min(x1, a.AxisX.PixelPositionToValue(e.X) * chartDiv));
                    pendingStart = c - (c - x0) * (w / (x1 - x0));
                }
                zoomWin = w;
            }
        }
        catch (Exception) { return; } // grafik henuz cizilmedi
        RefreshUi();
    }

    void ResetZoom()
    {
        zoomWin = 0; yLo = yHi = double.NaN; pendingStart = double.NaN; follow = true;
        RefreshUi();
    }

    // Yazi kutusunda yazarken fare grafigin ustunden gecerse odagi calma
    void FocusChart()
    {
        Control c = ActiveControl;
        while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
        if (!(c is TextBoxBase)) chart.Focus();
    }

    // Farenin altindaki anin degerini grafigin uzerinde gosterir; canli veri akarken her yenilemede guncellenir
    void UpdateHover()
    {
        if (!hovering) { lblHover.Visible = false; return; }
        ChartArea a = chart.ChartAreas[0];
        double x;
        try { x = a.AxisX.PixelPositionToValue(hoverPt.X); }
        catch (Exception) { lblHover.Visible = false; return; } // grafik henuz cizilmedi
        if (double.IsNaN(a.AxisX.Minimum) || x < a.AxisX.Minimum || x > a.AxisX.Maximum) { lblHover.Visible = false; return; }
        double t = x * chartDiv;
        ParamInfo p = (ParamInfo)cmbChart.SelectedItem;
        StringBuilder sb = new StringBuilder();
        lock (lk)
        {
            foreach (SeriesData s in series)
            {
                if (s.P != p || s.T.Count == 0) continue;
                int i = s.T.BinarySearch(t);
                if (i < 0) i = Math.Min(~i, s.T.Count - 1);
                if (i > 0 && Math.Abs(s.T[i - 1] - t) < Math.Abs(s.T[i] - t)) i--;
                // fare verinin olmadigi bos bolgedeyse (ornegin henuz dolmamis sag taraf) bir sey gosterme
                if (Math.Abs(s.T[i] - t) > Math.Max(1.0, WindowSeconds() / 100)) continue;
                if (sb.Length == 0) sb.Append(s.T[i] < 60 ? s.T[i].ToString("0.0", Cur) + " s" : Span(s.T[i]));
                sb.Append("\n" + s.Ch + "  " + Eng(s.V[i], s.P.Unit));
            }
        }
        lock (lk)
        {
            foreach (string key in serKeys)
            {
                if (!serShown.Contains(key)) continue;
                SeriesData s = serSeries[key];
                if (s.T.Count == 0) continue;
                int i = s.T.BinarySearch(t);
                if (i < 0) i = Math.Min(~i, s.T.Count - 1);
                if (i > 0 && Math.Abs(s.T[i - 1] - t) < Math.Abs(s.T[i] - t)) i--;
                if (Math.Abs(s.T[i] - t) > Math.Max(1.0, WindowSeconds() / 100)) continue;
                if (sb.Length == 0) sb.Append(s.T[i] < 60 ? s.T[i].ToString("0.0", Cur) + " s" : Span(s.T[i]));
                sb.Append("\n" + key + "  " + SerNum(s.V[i]));
            }
        }
        if (sb.Length == 0) { lblHover.Visible = false; return; }
        HoverTip.Show(lblHover, sb.ToString(), hoverPt);
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
            if (lo < 0) { x.Add(T[i]); y.Add(double.NaN); continue; } // gecersiz olcum ya da duraklama: cizgide bosluk
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
            List<string> ini = new List<string>(new string[] {
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
                "olay_yukseklik=" + logHeight,
                "test_adi=" + testName,
                "kayit_yeri=" + recBase,
                "kayit_klasoru=" + recName,
                "dil=" + (Ui.En ? "en" : "tr"),
                "tema=" + (Ui.Th.Dark ? "koyu" : "acik"),
            });
            SerialSaveSettings(ini);
            File.WriteAllLines(IniPath, ini.ToArray());
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
        if (d.TryGetValue("kayit_yeri", out v) && v.Length > 0) recBase = v;
        if (d.TryGetValue("kayit_klasoru", out v) && v.Length > 0) recName = v;
        if (d.TryGetValue("adres", out v)) txtAddr.Text = v;
        if (d.TryGetValue("baglanti", out v)) cmbConn.SelectedIndex = v == "ag" ? 1 : 0;
        if (d.TryGetValue("komut_seti", out v) && int.TryParse(v, out n) && n >= 0 && n < cmbDialect.Items.Count) cmbDialect.SelectedIndex = n;
    }

    // --selftest <png> [saniye] [toggle]: olcumu baslatir, bir sure sonra pencerenin goruntusunu kaydedip kapanir (gelistirme icin).
    // "toggle": once dil ve tema calisirken degistirilir. "scroll": goruntuden once grafik gecmisin basina kaydirilir.
    public void SelfTest(string png, int seconds, string mode)
    {
        testNoAsk = true; // kapatma ve diger onaylar test sirasinda sorulmaz
        Shown += delegate
        {
            if (mode == "recdlg")
            {
                // Kayit yeri penceresini acip goruntusunu al (pencere kipli oldugu icin zamanlayici onu disaridan yakalar)
                System.Windows.Forms.Timer dt = new System.Windows.Forms.Timer();
                dt.Interval = 1200;
                dt.Tick += delegate
                {
                    dt.Stop();
                    Form d = Application.OpenForms[Application.OpenForms.Count - 1];
                    d.Refresh(); Application.DoEvents(); Thread.Sleep(300);
                    using (Bitmap b = new Bitmap(d.Width, d.Height))
                    {
                        using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(d.Location, Point.Empty, d.Size);
                        b.Save(png);
                    }
                    d.Close(); Close();
                };
                dt.Start();
                RecordSettings();
                return;
            }
            if (mode == "defaults") { testNoAsk = true; ResetDefaults(); }
            if (mode == "toggle") { cmbLang.SelectedIndex = 1 - cmbLang.SelectedIndex; Application.DoEvents(); cmbTheme.SelectedIndex = 1 - cmbTheme.SelectedIndex; Application.DoEvents(); }
            if (mode != "seronly") StartMeasure(); // "seronly": yalnizca seri port, olcum yok
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate
            {
                t.Stop();
                if (mode == "scroll") scroll.Value = 0; // gecmisin basina kaydir
                if (mode == "hover") { hoverPt = new Point(chart.Width / 3, chart.Height / 2); hovering = true; }
                if (mode == "zoom")
                {
                    // tekerlek olaylarini taklit et: 3 kez zamanda, 2 kez (Ctrl yerine dogrudan) degerde yakinlastir
                    for (int z = 0; z < 3; z++) ChartWheel(new MouseEventArgs(MouseButtons.None, 0, chart.Width / 2, chart.Height / 2, 120));
                    ChartArea za = chart.ChartAreas[0];
                    double zl = za.AxisY.Minimum / lastK, zh = za.AxisY.Maximum / lastK, zc = (zl + zh) / 2;
                    yLo = zc - (zc - zl) * 0.36; yHi = zc + (zh - zc) * 0.36;
                }
                if (mode == "sersend") { ses[0].Send.Text = "TEST:123"; SerialSend(ses[0]); Application.DoEvents(); Thread.Sleep(400); RefreshUi(); }
                if (mode == "tools")
                {
                    // isaret koy, biraz daha olc, bir isaret daha, grafigi resim olarak kaydet
                    txtMark.Text = "yuk baglandi"; AddMark();
                    for (int z = 0; z < 8; z++) { Application.DoEvents(); Thread.Sleep(250); RefreshUi(); }
                    txtMark.Text = ""; AddMark();
                    for (int z = 0; z < 4; z++) { Application.DoEvents(); Thread.Sleep(250); RefreshUi(); }
                    SaveChartImage();
                }
                if (mode == "pause")
                {
                    // duraklat, 3 sn bekle, devam et, 4 sn daha olc: grafikte bosluk ve ayni dosyaya devam beklenir
                    StopMeasure(); for (int z = 0; z < 12; z++) { Application.DoEvents(); Thread.Sleep(250); RefreshUi(); }
                    StartMeasure(); for (int z = 0; z < 16; z++) { Application.DoEvents(); Thread.Sleep(250); RefreshUi(); }
                }
                if (mode == "stop") { EndTest(false); Application.DoEvents(); Thread.Sleep(600); Application.DoEvents(); } // durdurduktan sonraki olay listesi
                if (mode == "clear") { testNoAsk = true; ClearChart(); Application.DoEvents(); Thread.Sleep(1200); Application.DoEvents(); }
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
