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

class ParamInfo
{
    public string Code, Name, Unit;
    public ParamInfo(string code, string name, string unit) { Code = code; Name = name; Unit = unit; }
    public override string ToString() { return Code + "  –  " + Name; }

    public static readonly ParamInfo[] All = {
        new ParamInfo("RMS", "etkin değer", "V"),
        new ParamInfo("MEAN", "ortalama (DC)", "V"),
        new ParamInfo("PKPK", "tepe-tepe (Vpp)", "V"),
        new ParamInfo("MAX", "en yüksek", "V"),
        new ParamInfo("MIN", "en düşük", "V"),
        new ParamInfo("AMPL", "genlik", "V"),
        new ParamInfo("TOP", "üst seviye", "V"),
        new ParamInfo("BASE", "alt seviye", "V"),
        new ParamInfo("CRMS", "çevrim RMS", "V"),
        new ParamInfo("CMEAN", "çevrim ortalaması", "V"),
        new ParamInfo("FREQ", "frekans", "Hz"),
        new ParamInfo("PER", "periyot", "s"),
        new ParamInfo("DUTY", "doluluk oranı", "%"),
        new ParamInfo("PWID", "pozitif darbe genişliği", "s"),
        new ParamInfo("NWID", "negatif darbe genişliği", "s"),
        new ParamInfo("RISE", "yükselme süresi", "s"),
        new ParamInfo("FALL", "düşme süresi", "s"),
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

    public SeriesData(string ch, ParamInfo p) { Ch = ch; P = p; ResetStats(); }
    public string Key { get { return Ch + "_" + P.Code; } }

    public void ResetStats() { Min = double.MaxValue; Max = double.MinValue; Sum = 0; N = 0; }

    public void Add(double t, double v)
    {
        if (T.Count >= 200000) { T.RemoveRange(0, 50000); V.RemoveRange(0, 50000); }
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
    static readonly Color[] ChanColors = { Color.FromArgb(200, 150, 0), Color.FromArgb(194, 24, 91), Color.FromArgb(0, 151, 167), Color.FromArgb(46, 125, 50) };
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
    ComboBox cmbAlarmCh, cmbAlarmParam, cmbChart, cmbWindow, cmbConn, cmbDialect;
    TextBox txtLow, txtHigh, txtAddr;
    Label lblLow, lblHigh, lblDevice;
    FlowLayoutPanel cards;
    Chart chart;
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
        main.ColumnCount = 1; main.RowCount = 4;
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        main.Padding = new Padding(4);

        cards = new FlowLayoutPanel();
        cards.Dock = DockStyle.Fill; cards.AutoScroll = true;
        main.Controls.Add(cards, 0, 0);

        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Fill;
        bar.Controls.Add(MakeLabel("Grafik:", 6));
        cmbChart = MakeCombo(190);
        foreach (ParamInfo p in ParamInfo.All) cmbChart.Items.Add(p);
        cmbChart.SelectedIndex = 0;
        bar.Controls.Add(cmbChart);
        bar.Controls.Add(MakeLabel("Zaman aralığı:", 6));
        cmbWindow = MakeCombo(100);
        cmbWindow.Items.AddRange(new object[] { "30 sn", "2 dk", "10 dk", "1 saat" });
        cmbWindow.SelectedIndex = 1;
        bar.Controls.Add(cmbWindow);
        main.Controls.Add(bar, 0, 1);

        chart = new Chart();
        chart.Dock = DockStyle.Fill;
        ChartArea area = new ChartArea("a");
        area.AxisX.Title = "Süre [s]";
        area.AxisX.LabelStyle.Format = "0";
        area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
        area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
        area.AxisY.IsStartedFromZero = false;
        area.AxisY.LabelStyle.Format = "0.###";
        chart.ChartAreas.Add(area);
        Legend lg = new Legend("l");
        lg.Docking = Docking.Top;
        chart.Legends.Add(lg);
        main.Controls.Add(chart, 0, 2);

        lstLog = new ListBox();
        lstLog.Dock = DockStyle.Fill; lstLog.IntegralHeight = false;
        main.Controls.Add(lstLog, 0, 3);

        // sol panel
        Panel left = new Panel();
        left.Dock = DockStyle.Left; left.Width = 250; left.AutoScroll = true;
        left.Padding = new Padding(6);
        int y = 6;

        GroupBox gConn = MakeGroup("Bağlantı", left, ref y, 112);
        cmbConn = MakeCombo(70); cmbConn.Items.AddRange(new object[] { "USB", "Ağ" }); cmbConn.SelectedIndex = 0;
        cmbConn.SetBounds(12, 22, 70, 24);
        txtAddr = new TextBox(); txtAddr.SetBounds(88, 22, 138, 24);
        cmbConn.SelectedIndexChanged += delegate { txtAddr.Enabled = cmbConn.SelectedIndex == 1; };
        txtAddr.Enabled = false;
        Label lAddr = MakeLabel("Ağ için IP adresi (örn. 192.168.1.50:5025)", 0);
        lAddr.Font = new Font("Segoe UI", 7.5f); lAddr.ForeColor = Color.DimGray;
        lAddr.SetBounds(12, 50, 216, 16);
        Label lDia = MakeLabel("Komut seti:", 0); lDia.SetBounds(12, 78, 70, 20);
        cmbDialect = MakeCombo(140); cmbDialect.Items.AddRange(Dialect.Names); cmbDialect.SelectedIndex = 0;
        cmbDialect.SetBounds(86, 74, 140, 24);
        gConn.Controls.AddRange(new Control[] { cmbConn, txtAddr, lAddr, lDia, cmbDialect });

        GroupBox gCh = MakeGroup("Kanallar", left, ref y, 52);
        for (int i = 0; i < 4; i++)
        {
            chkChan[i] = new CheckBox();
            chkChan[i].Text = Chans[i]; chkChan[i].ForeColor = ChanColors[i];
            chkChan[i].Font = new Font(Font, FontStyle.Bold);
            chkChan[i].SetBounds(12 + i * 56, 22, 52, 22);
            gCh.Controls.Add(chkChan[i]);
        }
        chkChan[0].Checked = true;

        GroupBox gPar = MakeGroup("Ölçülecek parametreler", left, ref y, 190);
        lstParams = new CheckedListBox();
        lstParams.CheckOnClick = true; lstParams.IntegralHeight = false;
        lstParams.SetBounds(8, 22, 218, 160);
        foreach (ParamInfo p in ParamInfo.All) lstParams.Items.Add(p);
        lstParams.SetItemChecked(0, true);
        gPar.Controls.Add(lstParams);

        GroupBox gInt = MakeGroup("Okuma aralığı", left, ref y, 56);
        numInterval = new NumericUpDown();
        numInterval.Minimum = MinInterval; numInterval.Maximum = 60000; numInterval.Increment = 10; numInterval.Value = MinInterval;
        numInterval.SetBounds(12, 22, 80, 24);
        gInt.Controls.Add(numInterval);
        Label ms = MakeLabel("ms bekleme (en az " + MinInterval + ")", 0);
        ms.SetBounds(98, 25, 130, 20);
        gInt.Controls.Add(ms);

        GroupBox gAl = MakeGroup("Limit kontrolü (süreklilik)", left, ref y, 176);
        chkAlarm = new CheckBox(); chkAlarm.Text = "Limit dışına çıkınca uyar";
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
        chkBeep = new CheckBox(); chkBeep.Text = "Sesli uyarı"; chkBeep.Checked = true;
        chkBeep.SetBounds(12, 142, 210, 22);
        gAl.Controls.AddRange(new Control[] { chkAlarm, cmbAlarmCh, cmbAlarmParam, lblLow, txtLow, lblHigh, txtHigh, chkBeep });

        lockWhileRunning = new Control[] { gConn, gCh, gPar, gInt, gAl };

        // ust cubuk
        Panel top = new Panel();
        top.Dock = DockStyle.Top; top.Height = 58; top.BackColor = Color.FromArgb(245, 246, 248);
        btnStart = new Button();
        btnStart.SetBounds(10, 9, 150, 40);
        btnStart.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        btnStart.FlatStyle = FlatStyle.Flat; btnStart.ForeColor = Color.White;
        btnStart.Click += delegate { if (running) StopMeasure(); else StartMeasure(); };
        chkRecord = new CheckBox(); chkRecord.Text = "CSV dosyasına kaydet"; chkRecord.Checked = true;
        chkRecord.SetBounds(176, 19, 160, 22);
        chkRecord.CheckedChanged += delegate { wantRecord = chkRecord.Checked; };
        btnFolder = MakeButton("Kayıt klasörü", 340, delegate { Directory.CreateDirectory(RecDir); Process.Start(RecDir); });
        btnShot = MakeButton("Ekran görüntüsü al", 456, delegate { TakeShot(); });
        btnShot.Width = 130;
        btnReset = MakeButton("İstatistiği sıfırla", 592, delegate { lock (lk) { foreach (SeriesData s in series) s.ResetStats(); violations = 0; } });
        btnReset.Width = 120;
        lblDevice = new Label();
        lblDevice.Dock = DockStyle.Right; lblDevice.Width = 380;
        lblDevice.Padding = new Padding(0, 0, 10, 0);
        lblDevice.TextAlign = ContentAlignment.MiddleRight;
        top.Controls.AddRange(new Control[] { btnStart, chkRecord, btnFolder, btnShot, btnReset, lblDevice });

        StatusStrip st = new StatusStrip();
        stState = new ToolStripStatusLabel("Hazır");
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
        GroupBox g = new GroupBox();
        g.Text = text; g.SetBounds(6, y, 234, height);
        parent.Controls.Add(g);
        y += height + 8;
        return g;
    }

    void SetStartButton()
    {
        btnStart.Text = running ? "■  Durdur" : "▶  Başlat";
        btnStart.BackColor = running ? Color.FromArgb(198, 40, 40) : Color.FromArgb(46, 125, 50);
    }

    void UpdateLimitLabels()
    {
        string unit = ((ParamInfo)cmbAlarmParam.SelectedItem).Unit;
        lblLow.Text = "Alt limit [" + unit + "]:";
        lblHigh.Text = "Üst limit [" + unit + "]:";
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
            MessageBox.Show(this, "Ağ bağlantısı için osiloskobun IP adresini girin.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        for (int i = 0; i < 4; i++) if (chkChan[i].Checked) c.Chans.Add(Chans[i]);
        foreach (object o in lstParams.CheckedItems) c.Params.Add((ParamInfo)o);
        if (c.Chans.Count == 0 || c.Params.Count == 0)
        {
            MessageBox.Show(this, "En az bir kanal ve bir parametre seçin.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show(this, "Limit kontrolü için seçilen kanal ve parametre ölçülenler arasında olmalı ve en az bir limit girilmeli.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        lock (lk) { series = list; samples = 0; violations = 0; lastT = 0; }
        cfg = c;
        alarmOut = false;
        // grafikte secili parametre olculmuyorsa ilk olculene gec
        if (!c.Params.Contains((ParamInfo)cmbChart.SelectedItem)) cmbChart.SelectedItem = c.Params[0];
        BuildCards();
        startedAt = DateTime.Now;
        running = true;
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
        if (!running) return;
        running = false;
        if (worker != null) worker.Join(6000);
        worker = null;
        foreach (Control ctl in lockWhileRunning) ctl.Enabled = true;
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
            p.Size = new Size(206, 108); p.Margin = new Padding(4); p.BackColor = Color.White; p.BorderStyle = BorderStyle.FixedSingle;
            Label title = new Label();
            title.Text = s.Ch + "  " + s.P.Code + "  (" + s.P.Name + ")";
            title.ForeColor = ChanColors[Array.IndexOf(Chans, s.Ch)];
            title.Font = new Font(Font, FontStyle.Bold);
            title.SetBounds(8, 6, 192, 18); title.AutoEllipsis = true;
            Label val = new Label();
            val.Font = new Font("Segoe UI", 19f, FontStyle.Bold);
            val.SetBounds(6, 24, 194, 40); val.Text = "—";
            Label stat = new Label();
            stat.Font = new Font("Segoe UI", 8f); stat.ForeColor = Color.DimGray;
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
        if (d.ScreenshotCmd == null) { Log("Ekran görüntüsü bu komut setinde (" + d.Name + ") desteklenmiyor"); return; }
        u.SetTimeout(15000);
        try { File.WriteAllBytes(basePath + d.ScreenshotExt, u.Query(d.ScreenshotCmd)); }
        finally { u.SetTimeout(3000); }
        Log("Ekran görüntüsü kaydedildi: " + Path.GetFileName(basePath + d.ScreenshotExt));
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
        Log("Ölçüm başladı");
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
                            connected = false; deviceText = "Osiloskop bulunamadı – bekleniyor…";
                            Thread.Sleep(1000);
                            continue;
                        }
                        u.Clear(); u.SetTimeout(3000);
                        string idn = u.QueryText("*IDN?");
                        dl = Dialect.Pick(c.DialectIdx, idn);
                        deviceText = ShortIdn(idn);
                        connected = true;
                        Log("Bağlandı: " + deviceText + "  –  komut seti: " + dl.Name);
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
                        StringBuilder h = new StringBuilder("zaman" + Sep + "t[s]");
                        foreach (SeriesData s in series) h.Append(Sep + s.Key + "[" + s.P.Unit + "]");
                        csv.WriteLine(h);
                        if (c.AlarmIdx >= 0)
                        {
                            evCsv = new StreamWriter(name + "_olaylar.csv", false, new UTF8Encoding(true));
                            evCsv.AutoFlush = true;
                            evCsv.WriteLine("zaman" + Sep + "t[s]" + Sep + "olay" + Sep + "deger" + Sep + "limit disi sure[s]");
                        }
                        recFile = Path.GetFileName(name + ".csv");
                        Log("Kayıt: " + recFile);
                    }
                    else if (!wantRecord && csv != null)
                    {
                        csv.Close(); csv = null;
                        if (evCsv != null) { evCsv.Close(); evCsv = null; }
                        recFile = "";
                        Log("Kayıt durduruldu");
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
                            Log("LİMİT DIŞI  " + s.Ch + " " + s.P.Code + " = " + Eng(v, s.P.Unit));
                            if (evCsv != null) evCsv.WriteLine(stamp + Sep + "limit disi" + Sep + Plain(v, 4) + Sep);
                            if (c.Beep) SystemSounds.Exclamation.Play();
                        }
                        else if (!bad && alarmOut)
                        {
                            alarmOut = false;
                            Log("Normale döndü  " + s.Ch + " " + s.P.Code + " = " + Eng(v, s.P.Unit) + "  (limit dışı süre " + (t - outSince).ToString("F1", Cur) + " sn)");
                            if (evCsv != null) evCsv.WriteLine(stamp + Sep + "normale dondu" + Sep + Plain(v, 4) + Sep + (t - outSince).ToString("F2", Cur));
                        }
                    }
                    Thread.Sleep(Math.Max(c.Interval, MinInterval));
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is TimeoutException)) throw;
                    errs++;
                    Log("İletişim hatası: " + e.Message);
                    if (errs >= 3 && u != null)
                    {
                        u.Dispose(); u = null;
                        connected = false; deviceText = "Bağlantı koptu – yeniden deneniyor…";
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
        catch (Exception e) { Log("Beklenmeyen hata: " + e.Message); running = false; }
        finally
        {
            if (csv != null) csv.Close();
            if (evCsv != null) evCsv.Close();
            if (u != null) u.Dispose();
            recFile = "";
            Log("Ölçüm durdu");
        }
    }

    static string ShortIdn(string idn)
    {
        string[] p = idn.Split(',');
        return p.Length >= 3 ? p[1] + "  (seri no " + p[2] + ")" : idn;
    }

    // Olcum calismiyorken cihaza kisa sureligine baglanir
    bool WithDevice(Config c, Action<ILink, Dialect> act)
    {
        try
        {
            using (ILink u = OpenLink(c))
            {
                if (u == null) { connected = false; deviceText = "Osiloskop bulunamadı (bağlı mı, başka program kullanıyor mu?)"; return false; }
                u.Clear(); u.SetTimeout(3000);
                string idn = u.QueryText("*IDN?");
                deviceText = ShortIdn(idn);
                connected = true;
                if (act != null) act(u, Dialect.Pick(c.DialectIdx, idn));
                return true;
            }
        }
        catch (Exception e) { Log("Hata: " + e.Message); return false; }
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
        switch (cmbWindow.SelectedIndex) { case 0: return 30; case 1: return 120; case 2: return 600; }
        return 3600;
    }

    void RefreshUi()
    {
        lock (logQueue)
            while (logQueue.Count > 0)
            {
                lstLog.Items.Insert(0, logQueue.Dequeue());
                if (lstLog.Items.Count > 1000) lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
            }
        if (!running && btnStart.Text.Contains("Durdur")) StopMeasure(); // is parcacigi kendi durduysa

        lblDevice.Text = deviceText;
        lblDevice.ForeColor = connected ? Color.FromArgb(46, 125, 50) : Color.FromArgb(198, 40, 40);

        long n, viol; double tNow, rate = 0;
        lock (lk)
        {
            n = samples; viol = violations; tNow = lastT;
            for (int i = 0; i < series.Count && cardValue != null && i < cardValue.Length; i++)
            {
                SeriesData s = series[i];
                cardValue[i].Text = Eng(s.Last, s.P.Unit);
                cardStat[i].Text = s.N == 0 ? "" :
                    "min " + Eng(s.Min, s.P.Unit) + "   maks " + Eng(s.Max, s.P.Unit) + "\nortalama " + Eng(s.Sum / s.N, s.P.Unit);
                bool alarm = cfg != null && i == cfg.AlarmIdx && alarmOut && running;
                cardPanel[i].BackColor = alarm ? Color.FromArgb(255, 205, 210) : Color.White;
            }
            if (series.Count > 0)
            {
                List<double> T = series[0].T;
                int m = Math.Min(T.Count, 20);
                if (m >= 2 && T[T.Count - 1] > T[T.Count - m]) rate = (m - 1) / (T[T.Count - 1] - T[T.Count - m]);
            }
        }
        stState.Text = running ? (connected ? "Ölçülüyor" : "Cihaz bekleniyor") : "Hazır";
        stCount.Text = n > 0 ? n + " okuma" : "";
        stRate.Text = running && rate > 0 ? rate.ToString("F1", Cur) + " okuma/sn" : "";
        stTime.Text = running ? "Süre " + Span((DateTime.Now - startedAt).TotalSeconds) : "";
        stAlarm.Text = cfg != null && cfg.AlarmIdx >= 0 && n > 0 ? "Limit dışı: " + viol + " kez" : "";
        stAlarm.ForeColor = viol > 0 ? Color.FromArgb(198, 40, 40) : SystemColors.ControlText;
        stFile.Text = recFile.Length > 0 ? "Kayıt: kayitlar\\" + recFile : "";
        RefreshChart(tNow);
    }

    void RefreshChart(double tNow)
    {
        ParamInfo p = (ParamInfo)cmbChart.SelectedItem;
        double win = WindowSeconds();
        double t0 = Math.Max(0, tNow - win);
        List<string> names = new List<string>();
        List<double[]> xs = new List<double[]>(), ys = new List<double[]>();
        double maxAbs = 0;
        lock (lk)
        {
            foreach (SeriesData s in series)
            {
                if (s.P != p) continue;
                int i0 = s.T.BinarySearch(t0);
                if (i0 < 0) i0 = ~i0;
                List<double> x = new List<double>(), y = new List<double>();
                Decimate(s.T, s.V, i0, s.T.Count, x, y);
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
        area.AxisX.Minimum = Math.Floor(t0);
        area.AxisX.Maximum = Math.Max(Math.Ceiling(tNow), Math.Floor(t0) + Math.Min(win, 10));

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
            c.Color = ChanColors[Array.IndexOf(Chans, names[i])];
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
                sl.BorderColor = Color.FromArgb(198, 40, 40); sl.BorderWidth = 1; sl.BorderDashStyle = ChartDashStyle.Dash;
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

    // --selftest <png> [saniye]: olcumu baslatir, bir sure sonra pencerenin goruntusunu kaydedip kapanir (gelistirme icin)
    public void SelfTest(string png, int seconds)
    {
        Shown += delegate
        {
            StartMeasure();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = seconds * 1000;
            t.Tick += delegate
            {
                t.Stop();
                RefreshUi();
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(png); }
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
        if (args.Length >= 2 && args[0] == "--selftest") f.SelfTest(args[1], args.Length > 2 ? int.Parse(args[2]) : 6);
        Application.Run(f);
    }
}
