// ScopeRec'in seri port paneli: ayni anda 3 baglantiya kadar gomulu terminal, gelen degerlerin grafige eklenmesi
// ve her baglantinin kendi kayit dosyasi.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

partial class MainForm
{
    // Seri porttan gelen degerler icin sozde parametre (birimsiz); seriler sag eksende cizilir
    static readonly ParamInfo SerParam = new ParamInfo("SER", "seri port", "serial", "");
    static readonly Color[] SerColors = { Color.FromArgb(120, 144, 220), Color.FromArgb(230, 140, 0), Color.FromArgb(160, 110, 200),
                                          Color.FromArgb(120, 170, 60), Color.FromArgb(220, 90, 90), Color.FromArgb(90, 190, 180) };
    const int SerCount = 3;      // ayni anda acilabilen seri baglanti sayisi
    const int MaxSerKeys = 32;   // baglanti basina izlenen deger adi

    // Bir seri baglantinin ayarlari, durumu ve (arayuz kuruluyken) denetimleri
    class SerSession
    {
        public int Index;                 // 0..2
        // ayarlar
        public string Port = "", Baud = "115200";
        public bool Rec = true, Stamp = true, Dtr, Rts, Auto;
        public int Eol = 2;
        // baglanti ve veri
        public SerialLink Link;
        public readonly Queue<string> Queue = new Queue<string>();   // ekrana yazilmayi bekleyen satirlar (kilit: Lock)
        public readonly StringBuilder Text = new StringBuilder();    // ekrandaki metin; arayuz yeniden kurulunca geri konur
        public readonly List<string> Keys = new List<string>();      // bu baglantidan gelen deger adlari (kilit: lk)
        public readonly object Lock = new object();
        public volatile string Closed;                               // baglanti kendiliginden koptuysa sebep
        // kopan baglantiyi kendiliginden yeniden acma
        public bool Retry;                                           // kopunca true; kullanici kesince false
        public int RetryAt;                                          // bir sonraki denemenin zamani (Environment.TickCount)
        public volatile bool Connecting;                             // arka planda deneme suruyor
        public volatile SerialLink Pending;                          // arka planda acilan baglanti; arayuz is parcacigi devralir
        public volatile bool StampNow, RecNow;
        // kayit (kilit: Lock)
        public StreamWriter Writer;
        public string WriterPath, StandalonePath, LastFile;
        // arayuz
        public Panel Page;
        public Button Tab, Conn;
        public ComboBox CmbPort, CmbBaud, CmbEol;
        public CheckBox ChkRec, ChkStamp, ChkDtr, ChkRts;
        public TextBox Log, Send;
        public ListView KeyList;

        // 2. ve 3. baglantinin deger adlari ve dosyalari numarayla ayrilir: "T#2", "<olcum>_seri2.txt"
        public string Suffix { get { return Index == 0 ? "" : "#" + (Index + 1); } }
        public string FileTag { get { return Index == 0 ? "_seri.txt" : "_seri" + (Index + 1) + ".txt"; } }
    }

    readonly SerSession[] ses = { new SerSession(), new SerSession(), new SerSession() };
    Panel serPanel;
    Splitter serSplit;
    Button btnSerToggle;
    Label lblSerState;
    bool serUiLoading, serPanelOn = true;
    int serWidth = 390, serActive;

    // grafik icin ortak veri (tum baglantilar)
    readonly Dictionary<string, SeriesData> serSeries = new Dictionary<string, SeriesData>(); // kilit: lk
    readonly List<string> serKeys = new List<string>();             // gorulme sirasiyla tum deger adlari (kilit: lk)
    readonly HashSet<string> serShown = new HashSet<string>();      // grafikte gosterilenler (yalnizca arayuz is parcacigi)
    double serLastT;                                                // kilit: lk
    volatile string measStem;                                       // olcum kaydi acikken dosya kok adi; seri kayitlar ayni ada yazilir

    // Olcum ve seri verinin ortak zaman ekseni. Olcum baslayinca sifirlanir.
    Stopwatch plotClock = Stopwatch.StartNew();

    void BuildSerialPanel()
    {
        for (int i = 0; i < SerCount; i++) ses[i].Index = i;
        serPanel = new Panel();
        serPanel.Size = new Size(serWidth, 700);
        int w = serWidth;

        Label title = new Label();
        title.Text = Ui.S("Seri port", "Serial port"); title.Font = new Font(Font, FontStyle.Bold); title.SetBounds(8, 8, 70, 18);
        lblSerState = new Label();
        lblSerState.SetBounds(w - 150, 8, 142, 18); lblSerState.Anchor = AnchorStyles.Top | AnchorStyles.Right; lblSerState.TextAlign = ContentAlignment.TopRight;
        serPanel.Controls.Add(title); serPanel.Controls.Add(lblSerState);

        // sekme dugmeleri: her baglanti icin bir tane
        for (int i = 0; i < SerCount; i++)
        {
            SerSession s = ses[i];
            s.Tab = new Button();
            s.Tab.FlatStyle = FlatStyle.Flat; s.Tab.SetBounds(8 + i * 124, 30, 120, 26); s.Tab.TextAlign = ContentAlignment.MiddleLeft;
            s.Tab.Click += delegate { serActive = s.Index; SerialTabs(); };
            serPanel.Controls.Add(s.Tab);
            BuildSerialPage(s, w);
            serPanel.Controls.Add(s.Page);
        }

        serPanel.Dock = DockStyle.Right;
        serSplit = new GripSplitter(DockStyle.Right);
        serSplit.MinSize = 390; serSplit.MinExtra = 300;
        serSplit.SplitterMoved += delegate { serWidth = serPanel.Width; };
        serPanel.Visible = serSplit.Visible = serPanelOn;
        // ayirici panelden once eklenir ki panelin solunda yer alsin (Dock: son eklenen once yerlesir)
        Controls.Add(serSplit);
        Controls.Add(serPanel);

        btnSerToggle = new Button();
        btnSerToggle.Text = Ui.S("Seri port", "Serial port"); btnSerToggle.SetBounds(976, 14, 86, 30);
        btnSerToggle.Click += delegate { serPanelOn = !serPanelOn; serPanel.Visible = serSplit.Visible = serPanelOn; };
        topBar.Controls.Add(btnSerToggle);
        btnSerToggle.BringToFront();

        // sag eksen: seri port degerleri
        ChartArea a = chart.ChartAreas[0];
        a.AxisY2.IsStartedFromZero = false; a.AxisY2.MajorGrid.Enabled = false; a.AxisY2.LabelStyle.Format = "0.###";
        a.AxisY2.Title = Ui.S("Seri port değerleri", "Serial values");
        a.AxisY2.Enabled = AxisEnabled.False;

        serActive = Math.Max(0, Math.Min(SerCount - 1, serActive));
        SerialTabs();
    }

    // Bir baglantinin sayfasi: port/hiz, baglan, secenekler, terminal, deger listesi, gonderme satiri
    void BuildSerialPage(SerSession s, int w)
    {
        Theme t = Ui.Th;
        AnchorStyles topWide = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        AnchorStyles bottomWide = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        const int H = 640;
        Panel p = new Panel();
        p.SetBounds(0, 60, w, H); p.Anchor = topWide | AnchorStyles.Bottom;
        s.Page = p;

        s.CmbPort = new ComboBox(); // elle de yazilabilir: COM adi ya da tcp://adres:port
        s.CmbPort.SetBounds(8, 4, w - 150, 24); s.CmbPort.Anchor = topWide; s.CmbPort.Text = s.Port;
        s.CmbPort.DropDown += delegate { FillPorts(s); };
        NoStickySelection(s.CmbPort);
        s.CmbBaud = new ComboBox();
        s.CmbBaud.Items.AddRange(new object[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200", "230400", "250000", "460800", "500000", "921600", "1000000", "2000000" });
        s.CmbBaud.Text = s.Baud; s.CmbBaud.SetBounds(w - 136, 4, 128, 24); s.CmbBaud.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        NoStickySelection(s.CmbBaud);

        s.Conn = new Button();
        s.Conn.SetBounds(8, 34, 110, 28);
        s.Conn.Click += delegate
        {
            if (s.Link != null) SerialDisconnect(s, null);
            else if (s.Retry) { s.Retry = false; SerialNote(s, Ui.S("Yeniden bağlanma durduruldu", "Stopped trying to reconnect")); SerialTabs(); }
            else SerialConnect(s);
        };
        s.ChkDtr = new CheckBox(); s.ChkDtr.Text = "DTR"; s.ChkDtr.Checked = s.Dtr; s.ChkDtr.SetBounds(128, 38, 52, 22);
        s.ChkRts = new CheckBox(); s.ChkRts.Text = "RTS"; s.ChkRts.Checked = s.Rts; s.ChkRts.SetBounds(182, 38, 52, 22);
        Button btnClear = new Button();
        btnClear.Text = Ui.S("Temizle", "Clear"); btnClear.SetBounds(w - 88, 34, 80, 28); btnClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClear.Click += delegate { lock (s.Lock) s.Queue.Clear(); s.Text.Length = 0; s.Log.Clear(); };

        s.ChkRec = new CheckBox(); s.ChkRec.Text = Ui.S("Dosyaya kaydet", "Record to file"); s.ChkRec.Checked = s.Rec; s.ChkRec.SetBounds(8, 68, 130, 22);
        s.ChkStamp = new CheckBox(); s.ChkStamp.Text = Ui.S("Zaman damgası", "Timestamps"); s.ChkStamp.Checked = s.Stamp; s.ChkStamp.SetBounds(142, 68, 130, 22);
        EventHandler opt = delegate
        {
            if (serUiLoading) return;
            s.Rec = s.ChkRec.Checked; s.Stamp = s.ChkStamp.Checked; s.Dtr = s.ChkDtr.Checked; s.Rts = s.ChkRts.Checked;
            s.RecNow = s.Rec; s.StampNow = s.Stamp;
        };
        s.ChkRec.CheckedChanged += opt; s.ChkStamp.CheckedChanged += opt; s.ChkDtr.CheckedChanged += opt; s.ChkRts.CheckedChanged += opt;

        int logBottom = H - 196;
        s.Log = new TextBox();
        s.Log.Multiline = true; s.Log.ReadOnly = true; s.Log.ScrollBars = ScrollBars.Both; s.Log.WordWrap = false;
        s.Log.Font = new Font("Consolas", 9f); s.Log.MaxLength = 0;
        s.Log.SetBounds(8, 96, w - 16, logBottom - 96); s.Log.Anchor = topWide | AnchorStyles.Bottom;
        s.Log.Text = s.Text.ToString();

        Label lKeys = new Label();
        lKeys.Text = Ui.S("Gelen değerler – işaretlenenler grafikte (sağ eksen)", "Incoming values – checked ones are charted (right axis)");
        lKeys.SetBounds(8, logBottom + 6, w - 16, 16); lKeys.Anchor = bottomWide; lKeys.ForeColor = t.Muted;
        s.KeyList = new ListView();
        s.KeyList.View = View.Details; s.KeyList.CheckBoxes = true; s.KeyList.FullRowSelect = true; s.KeyList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        s.KeyList.Columns.Add(Ui.S("Ad", "Name"), 110); s.KeyList.Columns.Add(Ui.S("Son değer", "Last value"), 100);
        s.KeyList.Columns.Add("Min", 70); s.KeyList.Columns.Add(Ui.S("Maks", "Max"), 70);
        s.KeyList.SetBounds(8, logBottom + 24, w - 16, 124); s.KeyList.Anchor = bottomWide;
        s.KeyList.ItemChecked += delegate(object o, ItemCheckedEventArgs e)
        {
            if (serUiLoading) return;
            if (e.Item.Checked) serShown.Add(e.Item.Text); else serShown.Remove(e.Item.Text);
        };

        s.Send = new TextBox();
        s.Send.SetBounds(8, H - 38, w - 16 - 150, 24); s.Send.Anchor = bottomWide;
        s.Send.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SerialSend(s); } };
        s.CmbEol = new ComboBox(); s.CmbEol.DropDownStyle = ComboBoxStyle.DropDownList;
        s.CmbEol.Items.AddRange(new object[] { Ui.S("ek yok", "no EOL"), "LF", "CR+LF", "CR" });
        s.CmbEol.SelectedIndex = Math.Max(0, Math.Min(3, s.Eol));
        s.CmbEol.SetBounds(w - 154, H - 38, 70, 24); s.CmbEol.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        s.CmbEol.SelectedIndexChanged += delegate { s.Eol = s.CmbEol.SelectedIndex; };
        Button btnSend = new Button();
        btnSend.Text = Ui.S("Gönder", "Send"); btnSend.SetBounds(w - 80, H - 40, 72, 28); btnSend.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnSend.Click += delegate { SerialSend(s); };

        p.Controls.AddRange(new Control[] { s.CmbPort, s.CmbBaud, s.Conn, s.ChkDtr, s.ChkRts, btnClear,
                                            s.ChkRec, s.ChkStamp, s.Log, lKeys, s.KeyList, s.Send, s.CmbEol, btnSend });

        s.RecNow = s.Rec; s.StampNow = s.Stamp;
        serUiLoading = true;
        lock (lk)
            foreach (string key in s.Keys)
            {
                ListViewItem it = new ListViewItem(key);
                it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                it.Checked = serShown.Contains(key);
                s.KeyList.Items.Add(it);
            }
        serUiLoading = false;
    }

    // Sekme dugmelerini, gorunen sayfayi ve baglan dugmelerini gunceller
    void SerialTabs()
    {
        Theme t = Ui.Th;
        int connected = 0;
        for (int i = 0; i < SerCount; i++)
        {
            SerSession s = ses[i];
            bool on = s.Link != null, active = i == serActive;
            if (on) connected++;
            s.Page.Visible = active;
            s.Tab.Text = (on ? "● " : s.Retry ? "◌ " : "○ ") + (i + 1) + (on ? ": " + ShortPort(s.Link.Name) : s.Port.Length > 0 ? ": " + ShortPort(s.Port) : "");
            s.Tab.ForeColor = on ? t.Good : s.Retry ? t.Bad : t.Text;
            s.Tab.BackColor = active ? t.Card : t.Back;
            s.Tab.FlatAppearance.BorderColor = active ? t.Good : t.Border;
            s.Tab.FlatAppearance.BorderSize = active ? 2 : 1;
            s.Conn.Text = on ? Ui.S("Bağlantıyı kes", "Disconnect") : s.Retry ? Ui.S("Denemeyi bırak", "Stop retrying") : Ui.S("Bağlan", "Connect");
            s.CmbPort.Enabled = s.CmbBaud.Enabled = s.ChkDtr.Enabled = s.ChkRts.Enabled = !on && !s.Retry;
        }
        lblSerState.Text = connected == 0 ? Ui.S("bağlı değil", "not connected") : connected + Ui.S(" bağlantı açık", connected == 1 ? " connection open" : " connections open");
        lblSerState.ForeColor = connected > 0 ? t.Good : t.Muted;
    }

    static string ShortPort(string name)
    {
        if (name.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
        return name.Length > 12 ? name.Substring(0, 11) + "…" : name;
    }

    // Yazilabilir acilir liste, boyutu degisince ya da gorunur olunca metnini kendiliginden seciyor (mavi kaliyor);
    // odak kutuda degilken secimi kaldirir.
    static void NoStickySelection(ComboBox c)
    {
        EventHandler clear = delegate
        {
            if (!c.IsHandleCreated || c.IsDisposed) return;
            c.BeginInvoke(new MethodInvoker(delegate { if (!c.IsDisposed && !c.Focused) c.SelectionLength = 0; }));
        };
        c.Resize += clear; c.VisibleChanged += clear; c.HandleCreated += clear; c.Leave += clear;
        c.EnabledChanged += clear; c.TextChanged += clear;
    }

    void SerialTheme()
    {
        Theme t = Ui.Th;
        Axis ax = chart.ChartAreas[0].AxisY2;
        ax.LineColor = t.Border; ax.MajorTickMark.LineColor = t.Border; ax.LabelStyle.ForeColor = t.Text; ax.TitleForeColor = t.Text;
        foreach (SerSession s in ses)
        {
            if (!t.Dark) continue;
            s.CmbPort.BackColor = s.CmbBaud.BackColor = t.Input; // yazilabilir listeler duz stilde tema rengini alir
            try { SetWindowTheme(s.KeyList.Handle, "DarkMode_Explorer", null); SetWindowTheme(s.Log.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }
        SerialTabs(); // sekme dugmelerinin renkleri genel boyamadan sonra verilir
    }

    void FillPorts(SerSession s)
    {
        string cur = s.CmbPort.Text;
        s.CmbPort.Items.Clear();
        foreach (KeyValuePair<string, string> p in SerialLink.ListPorts())
            s.CmbPort.Items.Add(p.Value.Length > 0 ? p.Key + "  –  " + p.Value : p.Key);
        s.CmbPort.Text = cur;
    }

    // Listeden secilen "COM5  –  USB-SERIAL CH340" yazisindan port adini ayirir
    static string PortName(SerSession s)
    {
        string text = s.CmbPort.Text.Trim();
        int cut = text.IndexOf("  –  ", StringComparison.Ordinal);
        return cut > 0 ? text.Substring(0, cut).Trim() : text;
    }

    void SerialNote(SerSession s, string msg)
    {
        lock (s.Lock) s.Queue.Enqueue("--- " + msg);
    }

    void SerialConnect(SerSession s)
    {
        string name = PortName(s);
        int baud;
        if (name.Length == 0) { SerialNote(s, Ui.S("Önce bir port seçin", "Choose a port first")); return; }
        if (!int.TryParse(s.CmbBaud.Text.Trim(), out baud) || baud <= 0) { SerialNote(s, Ui.S("Geçersiz baud hızı", "Invalid baud rate")); return; }
        foreach (SerSession o in ses)
            if (o != s && o.Link != null && string.Equals(o.Link.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                SerialNote(s, name + Ui.S(" zaten " + (o.Index + 1) + ". bağlantıda açık", " is already open in connection " + (o.Index + 1)));
                return;
            }
        s.Port = name; s.Baud = s.CmbBaud.Text.Trim();
        try
        {
            s.Closed = null;
            // Olcum yokken grafik bos ise zaman ekseni ilk baglanti anindan baslasin
            lock (lk) if (!running && serSeries.Count == 0 && samples == 0) plotClock = Stopwatch.StartNew();
            s.StandalonePath = Path.Combine(RecDir, "seri_" + name.Replace("tcp://", "tcp_").Replace(':', '_').Replace('/', '_') + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            s.Link = SerialLink.Open(name, baud, s.Dtr, s.Rts, delegate(string line) { OnSerialLine(s, line); }, delegate(string why) { s.Closed = why; });
            SerialNote(s, Ui.S("Bağlandı: ", "Connected: ") + name + (name.StartsWith("tcp://") ? "" : " @ " + baud));
            Log(Ui.S("Seri port " + (s.Index + 1) + " bağlandı: ", "Serial port " + (s.Index + 1) + " connected: ") + name);
        }
        catch (Exception e)
        {
            s.Link = null;
            SerialNote(s, Ui.S("Açılamadı: ", "Could not open: ") + name + " – " + e.Message);
        }
        SerialTabs();
        SaveSettings();
    }

    void SerialDisconnect(SerSession s, string why)
    {
        if (s.Link == null) return;
        s.Link.Dispose(); s.Link = null;
        // Kendiliginden koptuysa (kablo cikti, cihaz yeniden basladi) birkac saniyede bir yeniden denenir; kullanici kestiyse denenmez
        s.Retry = why != null; s.RetryAt = Environment.TickCount + 2000;
        string file;
        lock (s.Lock)
        {
            if (s.Writer != null) { s.Writer.Close(); s.Writer = null; }
            s.WriterPath = null;
            file = s.LastFile; s.LastFile = null;
        }
        string n = (s.Index + 1).ToString();
        SerialNote(s, why == null ? Ui.S("Bağlantı kesildi", "Disconnected") : Ui.S("Bağlantı koptu", "Connection lost") + (why.Length > 0 ? ": " + why : ""));
        if (why != null) SerialNote(s, Ui.S("Yeniden bağlanmak için bekleniyor…", "Waiting to reconnect…"));
        if (file != null) SerialNote(s, Ui.S("Seri kayıt dosyası: ", "Serial record file: ") + file);
        Log(why == null ? Ui.S("Seri port " + n + " bağlantısı kesildi", "Serial port " + n + " disconnected")
                        : Ui.S("Seri port " + n + " bağlantısı koptu", "Serial port " + n + " connection lost"));
        if (file != null) Log(Ui.S("Seri kayıt dosyası: ", "Serial record file: ") + file);
        if (serPanel != null && !serPanel.IsDisposed) SerialTabs();
    }

    // Kopan baglanti icin: arka planda acmayi dener, acilinca arayuz is parcaciginda devralir
    void SerialRetry(SerSession s)
    {
        SerialLink pend = s.Pending;
        if (pend != null)
        {
            s.Pending = null;
            if (!s.Retry || s.Link != null) pend.Dispose(); // bu arada kullanici vazgecti ya da elle baglandi
            else
            {
                s.Link = pend; s.Retry = false; s.Closed = null;
                SerialNote(s, Ui.S("Yeniden bağlandı: ", "Reconnected: ") + pend.Name);
                Log(Ui.S("Seri port " + (s.Index + 1) + " yeniden bağlandı: ", "Serial port " + (s.Index + 1) + " reconnected: ") + pend.Name);
                SerialTabs();
            }
        }
        int baud;
        if (!s.Retry || s.Link != null || s.Connecting || Environment.TickCount - s.RetryAt < 0 || !int.TryParse(s.Baud, out baud)) return;
        s.Connecting = true;
        string name = s.Port; bool dtr = s.Dtr, rts = s.Rts;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { s.Pending = SerialLink.Open(name, baud, dtr, rts, delegate(string line) { OnSerialLine(s, line); }, delegate(string why) { s.Closed = why; }); }
            catch (Exception) { } // port henuz yok: sonraki denemede
            finally { s.RetryAt = Environment.TickCount + 2000; s.Connecting = false; }
        });
    }

    void SerialDisconnectAll()
    {
        foreach (SerSession s in ses) SerialDisconnect(s, null);
    }

    // Gecen sefer acik olan baglantilari yeniden acar
    void SerialAutoConnect()
    {
        foreach (SerSession s in ses) if (s.Auto) { s.Auto = false; SerialConnect(s); }
    }

    void SerialSend(SerSession s)
    {
        if (s.Link == null) { SerialNote(s, Ui.S("Bağlı değil", "Not connected")); return; }
        string text = s.Send.Text;
        string[] eol = { "", "\n", "\r\n", "\r" };
        try
        {
            s.Link.Write(text + eol[Math.Max(0, Math.Min(3, s.Eol))]);
            lock (s.Lock) s.Queue.Enqueue("> " + text);
            s.Send.SelectAll();
        }
        catch (Exception e) { SerialNote(s, Ui.S("Gönderilemedi: ", "Send failed: ") + e.Message); }
    }

    // Okuyucu is parcaciginda: satiri ekrana, grafige ve kayit dosyasina dagitir
    void OnSerialLine(SerSession s, string line)
    {
        double t = plotClock.Elapsed.TotalSeconds;
        DateTime now = DateTime.Now;
        List<KeyValuePair<string, double>> kv = SerialParse.Pairs(line);
        lock (lk)
        {
            foreach (KeyValuePair<string, double> p in kv)
            {
                string key = p.Key + s.Suffix;
                SeriesData d;
                if (!serSeries.TryGetValue(key, out d))
                {
                    if (s.Keys.Count >= MaxSerKeys) continue;
                    d = new SeriesData(key, SerParam);
                    serSeries[key] = d; serKeys.Add(key); s.Keys.Add(key);
                }
                d.Add(t, p.Value);
            }
            serLastT = t;
        }
        lock (s.Lock)
        {
            s.Queue.Enqueue((s.StampNow ? now.ToString("HH:mm:ss.fff") + "  " : "") + line);
            while (s.Queue.Count > 5000) s.Queue.Dequeue(); // arayuz yetisemezse en eskiler ekrana yazilmaz (dosyaya yazilir)
            WriteSerialRecord(s, now, t, line);
        }
    }

    // s.Lock altinda cagrilir. Olcum kaydi acikken <olcum>_seri[N].txt, degilken baglantinin kendi dosyasi.
    void WriteSerialRecord(SerSession s, DateTime now, double t, string line)
    {
        string stem = measStem;
        // Olcum kaydi acilmak uzereyken (olcum suruyor, CSV kaydi isaretli ama dosya henuz acilmadi) ayri dosya baslatma
        string target = !s.RecNow ? null : stem != null ? stem + s.FileTag : (running && wantRecord) ? null : s.StandalonePath;
        try
        {
            if (target != s.WriterPath)
            {
                if (s.Writer != null) { s.Writer.Close(); s.Writer = null; }
                s.WriterPath = target;
            }
            if (target == null) return;
            if (s.Writer == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                s.Writer = new StreamWriter(target, true, new UTF8Encoding(true));
                s.Writer.AutoFlush = true;
                s.LastFile = target;
            }
            s.Writer.WriteLine(now.ToString("dd.MM.yyyy HH:mm:ss.fff") + "\t" + t.ToString("F3", Cur) + "\t" + line);
        }
        catch (Exception e)
        {
            // disk dolu / klasor silinmis gibi durumlarda kaydi birak, terminal calismaya devam etsin
            s.RecNow = false; s.Writer = null; s.WriterPath = null;
            s.Queue.Enqueue("--- " + Ui.S("Kayıt yazılamadı, kayıt durduruldu: ", "Could not write the record, recording stopped: ") + e.Message);
        }
    }

    void ClearSerialSeries()
    {
        lock (lk) { foreach (SeriesData d in serSeries.Values) { d.T.Clear(); d.V.Clear(); d.ResetStats(); } serLastT = 0; }
    }

    // Arayuz zamanlayicisinda: bekleyen satirlari ekrana yazar, deger listelerini gunceller
    void RefreshSerial()
    {
        foreach (SerSession s in ses)
        {
            string why = s.Closed;
            if (why != null && s.Link != null) { s.Closed = null; SerialDisconnect(s, why); }
            SerialRetry(s);

            StringBuilder add = null;
            lock (s.Lock)
                if (s.Queue.Count > 0)
                {
                    add = new StringBuilder();
                    while (s.Queue.Count > 0) add.Append(s.Queue.Dequeue()).Append("\r\n");
                }
            if (add != null)
            {
                s.Text.Append(add);
                if (s.Text.Length > 400000)
                {
                    // cok uzadiysa eski yarisini at (dosyadaki kayit tam kalir)
                    s.Text.Remove(0, s.Text.Length - 200000);
                    s.Log.Text = s.Text.ToString();
                }
                else s.Log.AppendText(add.ToString());
                s.Log.SelectionStart = s.Log.TextLength;
                s.Log.ScrollToCaret();
            }

            lock (lk)
            {
                serUiLoading = true;
                for (int i = s.KeyList.Items.Count; i < s.Keys.Count; i++)
                {
                    ListViewItem it = new ListViewItem(s.Keys[i]);
                    it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                    it.Checked = serShown.Contains(s.Keys[i]); // onceki oturumda grafikte gosterilenler
                    s.KeyList.Items.Add(it);
                }
                serUiLoading = false;
                for (int i = 0; i < s.KeyList.Items.Count && i < s.Keys.Count; i++)
                {
                    SeriesData d = serSeries[s.Keys[i]];
                    ListViewItem it = s.KeyList.Items[i];
                    SetSub(it, 1, SerNum(d.Last));
                    SetSub(it, 2, d.N > 0 ? SerNum(d.Min) : "");
                    SetSub(it, 3, d.N > 0 ? SerNum(d.Max) : "");
                }
            }
        }
    }

    static void SetSub(ListViewItem it, int i, string text)
    {
        if (it.SubItems[i].Text != text) it.SubItems[i].Text = text; // degismediyse dokunma: liste titremesin
    }

    static string SerNum(double v)
    {
        return double.IsNaN(v) ? "" : v.ToString("0.####", Cur);
    }

    void SerialSaveSettings(List<string> lines)
    {
        foreach (SerSession s in ses)
        {
            if (s.CmbPort != null && !s.CmbPort.IsDisposed) { s.Port = PortName(s); s.Baud = s.CmbBaud.Text.Trim(); }
            string k = "seri" + (s.Index + 1) + "_";
            lines.Add(k + "port=" + s.Port);
            lines.Add(k + "baud=" + s.Baud);
            lines.Add(k + "kayit=" + (s.Rec ? "1" : "0"));
            lines.Add(k + "damga=" + (s.Stamp ? "1" : "0"));
            lines.Add(k + "dtr=" + (s.Dtr ? "1" : "0"));
            lines.Add(k + "rts=" + (s.Rts ? "1" : "0"));
            lines.Add(k + "satir_sonu=" + s.Eol);
            lines.Add(k + "bagli=" + (s.Link != null || s.Retry ? "1" : "0"));
        }
        lines.Add("seri_grafik=" + string.Join(",", new List<string>(serShown).ToArray()));
        lines.Add("seri_panel=" + (serPanelOn ? "1" : "0"));
        lines.Add("seri_genislik=" + serWidth);
        lines.Add("seri_sekme=" + serActive);
    }

    // Arayuz kurulmadan once cagrilir
    void SerialLoadSettings(Dictionary<string, string> d)
    {
        string v; int n;
        for (int i = 0; i < SerCount; i++)
        {
            SerSession s = ses[i];
            s.Index = i;
            // ilk baglanti icin eski surumun "seri_port" gibi anahtarlari da okunur
            string[] pre = i == 0 ? new string[] { "seri_", "seri1_" } : new string[] { "seri" + (i + 1) + "_" };
            foreach (string k in pre)
            {
                if (d.TryGetValue(k + "port", out v)) s.Port = v;
                if (d.TryGetValue(k + "baud", out v) && v.Length > 0) s.Baud = v;
                if (d.TryGetValue(k + "kayit", out v)) s.Rec = v == "1";
                if (d.TryGetValue(k + "damga", out v)) s.Stamp = v == "1";
                if (d.TryGetValue(k + "dtr", out v)) s.Dtr = v == "1";
                if (d.TryGetValue(k + "rts", out v)) s.Rts = v == "1";
                if (d.TryGetValue(k + "satir_sonu", out v) && int.TryParse(v, out n)) s.Eol = n;
                if (d.TryGetValue(k + "bagli", out v)) s.Auto = v == "1";
            }
        }
        if (d.TryGetValue("seri_panel", out v)) serPanelOn = v == "1";
        if (d.TryGetValue("seri_genislik", out v) && int.TryParse(v, out n)) serWidth = Math.Max(390, Math.Min(900, n));
        if (d.TryGetValue("seri_sekme", out v) && int.TryParse(v, out n)) serActive = n;
        if (d.TryGetValue("seri_grafik", out v)) foreach (string k in v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) serShown.Add(k);
    }

    void SerialDefaults()
    {
        foreach (SerSession s in ses)
        {
            s.Port = ""; s.Baud = "115200"; s.Rec = true; s.Stamp = true; s.Dtr = false; s.Rts = false; s.Eol = 2; s.Auto = false;
        }
        serPanelOn = true; serWidth = 390; serActive = 0;
        serShown.Clear();
    }
}
