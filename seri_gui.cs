// ScopeRec'in seri port paneli: gomulu terminal, gelen degerlerin grafige eklenmesi ve seri kayit dosyasi.
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
    const int MaxSerKeys = 32;

    // arayuz
    Panel serPanel;
    Splitter serSplit;
    ComboBox cmbPort, cmbBaud, cmbEol;
    Button btnSerConn, btnSerToggle;
    CheckBox chkSerRec, chkSerStamp, chkDtr, chkRts;
    TextBox txtSerLog, txtSerSend;
    ListView lvKeys;
    Label lblSerState;
    bool serUiLoading;

    // ayarlar (arayuz bastan kurulunca buradan geri yuklenir)
    string serPort = "", serBaud = "115200";
    bool serRec = true, serStamp = true, serDtr, serRts, serPanelOn = true, serAuto;
    int serEol = 2, serWidth = 390;
    List<string> serPortNames = new List<string>();

    // baglanti ve veri
    SerialLink serial;
    readonly object serLock = new object();
    readonly Queue<string> serQueue = new Queue<string>();          // ekrana yazilmayi bekleyen satirlar
    readonly StringBuilder serText = new StringBuilder();           // ekrandaki metin (arayuz yeniden kurulunca geri konur)
    readonly Dictionary<string, SeriesData> serSeries = new Dictionary<string, SeriesData>(); // kilit: lk
    readonly List<string> serKeys = new List<string>();             // gorulme sirasiyla anahtarlar (kilit: lk)
    readonly HashSet<string> serShown = new HashSet<string>();      // grafikte gosterilenler (yalnizca arayuz is parcacigi)
    double serLastT;                                                // kilit: lk
    volatile string serClosed;                                      // baglanti kendiliginden koptuysa sebep
    volatile bool serStampNow, serRecNow;
    long serLines;

    // kayit
    StreamWriter serWriter;                                         // kilit: serLock
    string serWriterPath, serStandalonePath, serLastFile;
    volatile string measStem;                                       // olcum kaydi acikken dosya kok adi; seri kayit ayni ada yazilir

    // Olcum ve seri verinin ortak zaman ekseni. Olcum baslayinca sifirlanir.
    Stopwatch plotClock = Stopwatch.StartNew();

    void BuildSerialPanel()
    {
        Theme t = Ui.Th;
        serPanel = new Panel();
        serPanel.Size = new Size(serWidth, 700);
        serPanel.Padding = new Padding(4);
        AnchorStyles top = AnchorStyles.Top | AnchorStyles.Left, topWide = top | AnchorStyles.Right;
        AnchorStyles bottom = AnchorStyles.Bottom | AnchorStyles.Left, bottomWide = bottom | AnchorStyles.Right;
        int w = serWidth;

        Label title = new Label();
        title.Text = Ui.S("Seri port", "Serial port"); title.Font = new Font(Font, FontStyle.Bold); title.SetBounds(8, 8, 120, 18);
        lblSerState = new Label();
        lblSerState.SetBounds(130, 8, w - 138, 18); lblSerState.Anchor = topWide; lblSerState.TextAlign = ContentAlignment.TopRight;

        cmbPort = new ComboBox(); // elle de yazilabilir: COM adi ya da tcp://adres:port
        cmbPort.SetBounds(8, 32, w - 150, 24); cmbPort.Anchor = topWide; cmbPort.Text = serPort;
        cmbPort.DropDown += delegate { FillPorts(); };
        cmbBaud = new ComboBox();
        cmbBaud.Items.AddRange(new object[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "74880", "115200", "230400", "250000", "460800", "500000", "921600", "1000000", "2000000" });
        cmbBaud.Text = serBaud; cmbBaud.SetBounds(w - 136, 32, 128, 24); cmbBaud.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        btnSerConn = new Button();
        btnSerConn.SetBounds(8, 62, 110, 28);
        btnSerConn.Click += delegate { if (serial != null) SerialDisconnect(null); else SerialConnect(); };
        chkDtr = new CheckBox(); chkDtr.Text = "DTR"; chkDtr.Checked = serDtr; chkDtr.SetBounds(128, 66, 52, 22);
        chkRts = new CheckBox(); chkRts.Text = "RTS"; chkRts.Checked = serRts; chkRts.SetBounds(182, 66, 52, 22);
        Button btnClear = new Button();
        btnClear.Text = Ui.S("Temizle", "Clear"); btnClear.SetBounds(w - 88, 62, 80, 28); btnClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClear.Click += delegate { lock (serLock) serQueue.Clear(); serText.Length = 0; txtSerLog.Clear(); };

        chkSerRec = new CheckBox(); chkSerRec.Text = Ui.S("Dosyaya kaydet", "Record to file"); chkSerRec.Checked = serRec; chkSerRec.SetBounds(8, 96, 130, 22);
        chkSerStamp = new CheckBox(); chkSerStamp.Text = Ui.S("Zaman damgası", "Timestamps"); chkSerStamp.Checked = serStamp; chkSerStamp.SetBounds(142, 96, 130, 22);
        EventHandler opt = delegate
        {
            if (serUiLoading) return;
            serRec = chkSerRec.Checked; serStamp = chkSerStamp.Checked; serDtr = chkDtr.Checked; serRts = chkRts.Checked;
            serRecNow = serRec; serStampNow = serStamp;
        };
        chkSerRec.CheckedChanged += opt; chkSerStamp.CheckedChanged += opt; chkDtr.CheckedChanged += opt; chkRts.CheckedChanged += opt;

        int logBottom = 700 - 196;
        txtSerLog = new TextBox();
        txtSerLog.Multiline = true; txtSerLog.ReadOnly = true; txtSerLog.ScrollBars = ScrollBars.Both; txtSerLog.WordWrap = false;
        txtSerLog.Font = new Font("Consolas", 9f); txtSerLog.MaxLength = 0;
        txtSerLog.SetBounds(8, 124, w - 16, logBottom - 124); txtSerLog.Anchor = topWide | AnchorStyles.Bottom;
        txtSerLog.Text = serText.ToString();

        Label lKeys = new Label();
        lKeys.Text = Ui.S("Gelen değerler – işaretlenenler grafikte (sağ eksen)", "Incoming values – checked ones are charted (right axis)");
        lKeys.SetBounds(8, logBottom + 6, w - 16, 16); lKeys.Anchor = bottomWide; lKeys.ForeColor = t.Muted;
        lvKeys = new ListView();
        lvKeys.View = View.Details; lvKeys.CheckBoxes = true; lvKeys.FullRowSelect = true; lvKeys.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        lvKeys.Columns.Add(Ui.S("Ad", "Name"), 110); lvKeys.Columns.Add(Ui.S("Son değer", "Last value"), 100);
        lvKeys.Columns.Add("Min", 70); lvKeys.Columns.Add(Ui.S("Maks", "Max"), 70);
        lvKeys.SetBounds(8, logBottom + 24, w - 16, 124); lvKeys.Anchor = bottomWide;
        lvKeys.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
        {
            if (serUiLoading) return;
            if (e.Item.Checked) serShown.Add(e.Item.Text); else serShown.Remove(e.Item.Text);
        };

        txtSerSend = new TextBox();
        txtSerSend.SetBounds(8, 700 - 38, w - 16 - 150, 24); txtSerSend.Anchor = bottomWide;
        txtSerSend.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SerialSend(); } };
        cmbEol = new ComboBox(); cmbEol.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbEol.Items.AddRange(new object[] { Ui.S("ek yok", "no EOL"), "LF", "CR+LF", "CR" });
        cmbEol.SelectedIndex = Math.Max(0, Math.Min(3, serEol));
        cmbEol.SetBounds(w - 154, 700 - 38, 70, 24); cmbEol.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cmbEol.SelectedIndexChanged += delegate { serEol = cmbEol.SelectedIndex; };
        Button btnSend = new Button();
        btnSend.Text = Ui.S("Gönder", "Send"); btnSend.SetBounds(w - 80, 700 - 40, 72, 28); btnSend.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnSend.Click += delegate { SerialSend(); };

        serPanel.Controls.AddRange(new Control[] { title, lblSerState, cmbPort, cmbBaud, btnSerConn, chkDtr, chkRts, btnClear,
                                                   chkSerRec, chkSerStamp, txtSerLog, lKeys, lvKeys, txtSerSend, cmbEol, btnSend });
        serPanel.Dock = DockStyle.Right;
        serSplit = new Splitter();
        serSplit.Dock = DockStyle.Right; serSplit.Width = 5; serSplit.MinSize = 260; serSplit.MinExtra = 300;
        serSplit.SplitterMoved += delegate { serWidth = serPanel.Width; };
        serPanel.Visible = serSplit.Visible = serPanelOn;
        // ayirici panelden once eklenir ki panelin solunda yer alsin (Dock: son eklenen once yerlesir)
        Controls.Add(serSplit);
        Controls.Add(serPanel);

        btnSerToggle = new Button();
        btnSerToggle.Text = Ui.S("Seri port", "Serial port"); btnSerToggle.SetBounds(890, 14, 86, 30);
        btnSerToggle.Click += delegate { serPanelOn = !serPanelOn; serPanel.Visible = serSplit.Visible = serPanelOn; };
        topBar.Controls.Add(btnSerToggle);
        btnSerToggle.BringToFront();

        // sag eksen: seri port degerleri
        ChartArea a = chart.ChartAreas[0];
        a.AxisY2.IsStartedFromZero = false; a.AxisY2.MajorGrid.Enabled = false; a.AxisY2.LabelStyle.Format = "0.###";
        a.AxisY2.Title = Ui.S("Seri port değerleri", "Serial values");
        a.AxisY2.Enabled = AxisEnabled.False;

        serRecNow = serRec; serStampNow = serStamp;
        serUiLoading = true;
        lock (lk)
            foreach (string key in serKeys)
            {
                ListViewItem it = new ListViewItem(key);
                it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                it.Checked = serShown.Contains(key);
                lvKeys.Items.Add(it);
            }
        serUiLoading = false;
        SerialButtons();
    }

    void SerialTheme()
    {
        Theme t = Ui.Th;
        Axis ax = chart.ChartAreas[0].AxisY2;
        ax.LineColor = t.Border; ax.MajorTickMark.LineColor = t.Border; ax.LabelStyle.ForeColor = t.Text; ax.TitleForeColor = t.Text;
        if (t.Dark)
        {
            cmbPort.BackColor = cmbBaud.BackColor = t.Input; // yazilabilir listeler duz stilde tema rengini alir
            try { SetWindowTheme(lvKeys.Handle, "DarkMode_Explorer", null); SetWindowTheme(txtSerLog.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }
    }

    void FillPorts()
    {
        string cur = cmbPort.Text;
        List<KeyValuePair<string, string>> ports = SerialLink.ListPorts();
        cmbPort.Items.Clear(); serPortNames.Clear();
        foreach (KeyValuePair<string, string> p in ports)
        {
            cmbPort.Items.Add(p.Value.Length > 0 ? p.Key + "  –  " + p.Value : p.Key);
            serPortNames.Add(p.Key);
        }
        cmbPort.Text = cur;
    }

    // Listeden secilen "COM5  –  USB-SERIAL CH340" yazisindan port adini ayirir
    string PortName()
    {
        string s = cmbPort.Text.Trim();
        int cut = s.IndexOf("  –  ", StringComparison.Ordinal);
        return cut > 0 ? s.Substring(0, cut).Trim() : s;
    }

    void SerialButtons()
    {
        bool on = serial != null;
        btnSerConn.Text = on ? Ui.S("Bağlantıyı kes", "Disconnect") : Ui.S("Bağlan", "Connect");
        cmbPort.Enabled = cmbBaud.Enabled = chkDtr.Enabled = chkRts.Enabled = !on;
        lblSerState.Text = on ? Ui.S("bağlı: ", "connected: ") + serial.Name : Ui.S("bağlı değil", "not connected");
        lblSerState.ForeColor = on ? Ui.Th.Good : Ui.Th.Muted;
    }

    void SerialNote(string msg)
    {
        lock (serLock) serQueue.Enqueue("--- " + msg);
    }

    void SerialConnect()
    {
        string name = PortName();
        int baud;
        if (name.Length == 0) { SerialNote(Ui.S("Önce bir port seçin", "Choose a port first")); return; }
        if (!int.TryParse(cmbBaud.Text.Trim(), out baud) || baud <= 0) { SerialNote(Ui.S("Geçersiz baud hızı", "Invalid baud rate")); return; }
        serPort = name; serBaud = cmbBaud.Text.Trim();
        try
        {
            serClosed = null;
            // Olcum yokken grafik bos ise zaman ekseni baglanti anindan baslasin
            lock (lk) if (!running && serSeries.Count == 0 && samples == 0) plotClock = Stopwatch.StartNew();
            serStandalonePath = Path.Combine(RecDir, "seri_" + name.Replace("tcp://", "tcp_").Replace(':', '_').Replace('/', '_') + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            serial = SerialLink.Open(name, baud, serDtr, serRts, OnSerialLine, delegate(string why) { serClosed = why; });
            SerialNote(Ui.S("Bağlandı: ", "Connected: ") + name + (name.StartsWith("tcp://") ? "" : " @ " + baud));
            Log(Ui.S("Seri port bağlandı: ", "Serial port connected: ") + name);
        }
        catch (Exception e)
        {
            serial = null;
            SerialNote(Ui.S("Açılamadı: ", "Could not open: ") + name + " – " + e.Message);
        }
        SerialButtons();
        SaveSettings();
    }

    void SerialDisconnect(string why)
    {
        if (serial == null) return;
        serial.Dispose(); serial = null;
        string file;
        lock (serLock)
        {
            if (serWriter != null) { serWriter.Close(); serWriter = null; }
            serWriterPath = null;
            file = serLastFile; serLastFile = null;
        }
        SerialNote(why == null ? Ui.S("Bağlantı kesildi", "Disconnected") : Ui.S("Bağlantı koptu", "Connection lost") + (why.Length > 0 ? ": " + why : ""));
        if (file != null) SerialNote(Ui.S("Seri kayıt dosyası: ", "Serial record file: ") + file);
        Log(why == null ? Ui.S("Seri port bağlantısı kesildi", "Serial port disconnected") : Ui.S("Seri port bağlantısı koptu", "Serial port connection lost"));
        if (file != null) Log(Ui.S("Seri kayıt dosyası: ", "Serial record file: ") + file);
        if (serPanel != null && !serPanel.IsDisposed) SerialButtons();
    }

    void SerialSend()
    {
        if (serial == null) { SerialNote(Ui.S("Bağlı değil", "Not connected")); return; }
        string text = txtSerSend.Text;
        string[] eol = { "", "\n", "\r\n", "\r" };
        try
        {
            serial.Write(text + eol[Math.Max(0, Math.Min(3, serEol))]);
            lock (serLock) serQueue.Enqueue("> " + text);
            txtSerSend.SelectAll();
        }
        catch (Exception e) { SerialNote(Ui.S("Gönderilemedi: ", "Send failed: ") + e.Message); }
    }

    // Okuyucu is parcaciginda: satiri ekrana, grafige ve kayit dosyasina dagitir
    void OnSerialLine(string line)
    {
        double t = plotClock.Elapsed.TotalSeconds;
        DateTime now = DateTime.Now;
        List<KeyValuePair<string, double>> kv = SerialParse.Pairs(line);
        lock (lk)
        {
            foreach (KeyValuePair<string, double> p in kv)
            {
                SeriesData s;
                if (!serSeries.TryGetValue(p.Key, out s))
                {
                    if (serSeries.Count >= MaxSerKeys) continue;
                    s = new SeriesData(p.Key, SerParam);
                    serSeries[p.Key] = s; serKeys.Add(p.Key);
                }
                s.Add(t, p.Value);
            }
            serLastT = t;
        }
        lock (serLock)
        {
            serLines++;
            serQueue.Enqueue((serStampNow ? now.ToString("HH:mm:ss.fff") + "  " : "") + line);
            while (serQueue.Count > 5000) serQueue.Dequeue(); // arayuz yetisemezse en eskiler ekrana yazilmaz (dosyaya yazilir)
            WriteSerialRecord(now, t, line);
        }
    }

    // serLock altinda cagrilir. Olcum kaydi acikken <olcum>_seri.txt, degilken baglantinin kendi dosyasi.
    void WriteSerialRecord(DateTime now, double t, string line)
    {
        string stem = measStem;
        // Olcum kaydi acilmak uzereyken (olcum suruyor, CSV kaydi isaretli ama dosya henuz acilmadi) ayri dosya baslatma
        string target = !serRecNow ? null : stem != null ? stem + "_seri.txt" : (running && wantRecord) ? null : serStandalonePath;
        try
        {
            if (target != serWriterPath)
            {
                if (serWriter != null) { serWriter.Close(); serWriter = null; }
                serWriterPath = target;
            }
            if (target == null) return;
            if (serWriter == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                serWriter = new StreamWriter(target, true, new UTF8Encoding(true));
                serWriter.AutoFlush = true;
                serLastFile = target;
            }
            serWriter.WriteLine(now.ToString("dd.MM.yyyy HH:mm:ss.fff") + "\t" + t.ToString("F3", Cur) + "\t" + line);
        }
        catch (Exception e)
        {
            // disk dolu / klasor silinmis gibi durumlarda kaydi birak, terminal calismaya devam etsin
            serRecNow = false; serWriter = null; serWriterPath = null;
            serQueue.Enqueue("--- " + Ui.S("Kayıt yazılamadı, kayıt durduruldu: ", "Could not write the record, recording stopped: ") + e.Message);
        }
    }

    void ClearSerialSeries()
    {
        lock (lk) { foreach (SeriesData s in serSeries.Values) { s.T.Clear(); s.V.Clear(); s.ResetStats(); } serLastT = 0; }
    }

    // Arayuz zamanlayicisinda: bekleyen satirlari ekrana yazar, deger listesini gunceller
    void RefreshSerial()
    {
        string why = serClosed;
        if (why != null && serial != null) { serClosed = null; SerialDisconnect(why); }

        StringBuilder add = null;
        lock (serLock)
            if (serQueue.Count > 0)
            {
                add = new StringBuilder();
                while (serQueue.Count > 0) add.Append(serQueue.Dequeue()).Append("\r\n");
            }
        if (add != null)
        {
            serText.Append(add);
            if (serText.Length > 400000)
            {
                // cok uzadiysa eski yarisini at (dosyadaki kayit tam kalir)
                serText.Remove(0, serText.Length - 200000);
                txtSerLog.Text = serText.ToString();
            }
            else txtSerLog.AppendText(add.ToString());
            txtSerLog.SelectionStart = txtSerLog.TextLength;
            txtSerLog.ScrollToCaret();
        }

        lock (lk)
        {
            serUiLoading = true;
            for (int i = lvKeys.Items.Count; i < serKeys.Count; i++)
            {
                ListViewItem it = new ListViewItem(serKeys[i]);
                it.SubItems.Add(""); it.SubItems.Add(""); it.SubItems.Add("");
                it.Checked = serShown.Contains(serKeys[i]); // onceki oturumda grafikte gosterilenler
                lvKeys.Items.Add(it);
            }
            serUiLoading = false;
            for (int i = 0; i < lvKeys.Items.Count && i < serKeys.Count; i++)
            {
                SeriesData s = serSeries[serKeys[i]];
                ListViewItem it = lvKeys.Items[i];
                SetSub(it, 1, SerNum(s.Last));
                SetSub(it, 2, s.N > 0 ? SerNum(s.Min) : "");
                SetSub(it, 3, s.N > 0 ? SerNum(s.Max) : "");
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
        if (cmbPort != null && !cmbPort.IsDisposed) { serPort = PortName(); serBaud = cmbBaud.Text.Trim(); }
        lines.Add("seri_port=" + serPort);
        lines.Add("seri_baud=" + serBaud);
        lines.Add("seri_grafik=" + string.Join(",", new List<string>(serShown).ToArray()));
        lines.Add("seri_kayit=" + (serRec ? "1" : "0"));
        lines.Add("seri_damga=" + (serStamp ? "1" : "0"));
        lines.Add("seri_dtr=" + (serDtr ? "1" : "0"));
        lines.Add("seri_rts=" + (serRts ? "1" : "0"));
        lines.Add("seri_satir_sonu=" + serEol);
        lines.Add("seri_panel=" + (serPanelOn ? "1" : "0"));
        lines.Add("seri_genislik=" + serWidth);
        lines.Add("seri_bagli=" + (serial != null ? "1" : "0"));
    }

    // Arayuz kurulmadan once cagrilir
    void SerialLoadSettings(Dictionary<string, string> d)
    {
        string v; int n;
        if (d.TryGetValue("seri_port", out v)) serPort = v;
        if (d.TryGetValue("seri_baud", out v) && v.Length > 0) serBaud = v;
        if (d.TryGetValue("seri_kayit", out v)) serRec = v == "1";
        if (d.TryGetValue("seri_damga", out v)) serStamp = v == "1";
        if (d.TryGetValue("seri_dtr", out v)) serDtr = v == "1";
        if (d.TryGetValue("seri_rts", out v)) serRts = v == "1";
        if (d.TryGetValue("seri_satir_sonu", out v) && int.TryParse(v, out n)) serEol = n;
        if (d.TryGetValue("seri_panel", out v)) serPanelOn = v == "1";
        if (d.TryGetValue("seri_genislik", out v) && int.TryParse(v, out n)) serWidth = Math.Max(260, Math.Min(900, n));
        if (d.TryGetValue("seri_bagli", out v)) serAuto = v == "1";
        if (d.TryGetValue("seri_grafik", out v)) foreach (string k in v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) serShown.Add(k);
    }

    void SerialDefaults()
    {
        serPort = ""; serBaud = "115200"; serRec = true; serStamp = true; serDtr = false; serRts = false; serEol = 2; serPanelOn = true; serWidth = 390; serAuto = false;
        serShown.Clear();
    }
}
