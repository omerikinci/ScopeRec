// Seri port (COM) baglantisi, port listesi ve gelen satirlardan sayisal deger ayiklama.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Bir seri port (ya da "tcp://adres:port" bicimindeki ag uzerinden seri kopru) baglantisi.
// Gelen veri ayri bir is parcaciginda satirlara bolunur ve onLine ile bildirilir.
class SerialLink : IDisposable
{
    static readonly Encoding Latin1 = Encoding.GetEncoding(28591); // bayt = karakter; bozuk veri istisna firlatmaz
    SerialPort port;
    TcpClient tcp;
    Stream stream;
    Thread reader;
    volatile bool closing;
    public int ReadErrors; // toparlanan hat hatasi sayisi
    int readOk;
    Action<string> onLine, onClosed;
    public string Name;

    // Acilamazsa istisna firlatir (port mesgul, yok, erisim reddedildi...)
    public static SerialLink Open(string name, int baud, bool dtr, bool rts, Action<string> onLine, Action<string> onClosed)
    {
        SerialLink l = new SerialLink();
        l.Name = name; l.onLine = onLine; l.onClosed = onClosed;
        if (name.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            string hp = name.Substring(6);
            int colon = hp.LastIndexOf(':'), tcpPort;
            if (colon <= 0 || !int.TryParse(hp.Substring(colon + 1), out tcpPort)) throw new IOException("tcp://adres:port");
            TcpClient c = new TcpClient();
            IAsyncResult ar = c.BeginConnect(hp.Substring(0, colon), tcpPort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(3000)) { c.Close(); throw new IOException("zaman asimi"); }
            c.EndConnect(ar);
            l.tcp = c; l.stream = c.GetStream();
        }
        else
        {
            SerialPort p = new SerialPort(name, baud, Parity.None, 8, StopBits.One);
            p.DtrEnable = dtr; p.RtsEnable = rts;
            p.ReadTimeout = 500; p.WriteTimeout = 2000;
            p.Open();
            // USB-seri donusturucu calisirken cekilirse .NET'in sonlandiricisi islenmeyen istisnayla uygulamayi cokertiyor
            GC.SuppressFinalize(p.BaseStream);
            l.port = p; l.stream = p.BaseStream;
        }
        l.reader = new Thread(l.ReadLoop);
        l.reader.IsBackground = true;
        l.reader.Start();
        return l;
    }

    void ReadLoop()
    {
        byte[] buf = new byte[4096];
        StringBuilder sb = new StringBuilder();
        bool lastCr = false;
        int lastByte = Environment.TickCount;
        string reason = null;
        try
        {
            while (!closing)
            {
                int n;
                if (port != null)
                {
                    // Seri portta bekleyen (bloklayan) okuma kullanilmaz: CH340 ile denendiginde surucu bekleyen okumayi surekli
                    // "islem durduruldu" diye iptal ediyordu. Bunun yerine gelen bayt var mi diye bakilir, varsa okunur.
                    try
                    {
                        int avail = port.BytesToRead;
                        if (avail == 0)
                        {
                            // Satir sonu gelmeden duran veri (komut istemi, ya da yanlis baud hizinda gelen anlamsiz baytlar)
                            // 300 ms sonra oldugu gibi gosterilir; yoksa kullanici hicbir sey gelmiyor sanar
                            if (sb.Length > 0 && Environment.TickCount - lastByte > 300) { onLine(sb.ToString()); sb.Length = 0; }
                            Thread.Sleep(5);
                            continue;
                        }
                        lastByte = Environment.TickCount;
                        n = port.Read(buf, 0, Math.Min(avail, buf.Length));
                    }
                    catch (TimeoutException) { continue; }
                    catch (IOException)
                    {
                        // hat hatasi (cerceve/tasma, yanlis baud): port hala aciksa kopma degildir
                        if (closing || !port.IsOpen || ++ReadErrors - readOk > 500) throw;
                        Thread.Sleep(10);
                        continue;
                    }
                    readOk = ReadErrors; // basarili okuma: art arda hata sayaci sifirlanir
                }
                else n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) { if (tcp != null) break; continue; }
                for (int i = 0; i < n; i++)
                {
                    char ch = Latin1.GetChars(buf, i, 1)[0];
                    // satir sonu: LF, CR ya da CR+LF (CR'den hemen sonraki LF ikinci bos satir uretmesin)
                    if (ch == '\n' && lastCr) { lastCr = false; continue; }
                    lastCr = ch == '\r';
                    if (ch == '\n' || ch == '\r' || sb.Length >= 4000)
                    {
                        if (sb.Length > 0) onLine(sb.ToString()); // bos satirlar atlanir
                        sb.Length = 0;
                        if (ch == '\n' || ch == '\r') continue;
                    }
                    if (ch != '\0') sb.Append(ch);
                }
            }
        }
        catch (Exception e) { reason = e.Message; }
        if (!closing && onClosed != null) onClosed(reason ?? "");
    }

    public void Write(string text)
    {
        byte[] b = Latin1.GetBytes(text);
        stream.Write(b, 0, b.Length);
    }

    public void Dispose()
    {
        closing = true;
        try { if (port != null) port.Close(); } catch (Exception) { }
        try { if (tcp != null) tcp.Close(); } catch (Exception) { }
    }

    // Bilgisayardaki COM portlari: ad ve varsa aygit aciklamasi ("COM5", "USB-SERIAL CH340 (COM5)")
    public static List<KeyValuePair<string, string>> ListPorts()
    {
        Dictionary<string, string> desc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                       "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                foreach (System.Management.ManagementBaseObject o in s.Get())
                {
                    string name = o["Name"] as string ?? "";
                    Match m = Regex.Match(name, @"\((COM\d+)\)");
                    if (m.Success) desc[m.Groups[1].Value] = name.Replace(m.Value, "").Trim();
                }
        }
        catch (Exception) { } // aciklama alinamazsa yalnizca port adlari gosterilir
        List<string> names = new List<string>(SerialPort.GetPortNames());
        names.Sort(delegate(string a, string b) { return PortNo(a).CompareTo(PortNo(b)); });
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
        foreach (string n in names)
        {
            if (list.Exists(delegate(KeyValuePair<string, string> k) { return k.Key == n; })) continue;
            string d;
            list.Add(new KeyValuePair<string, string>(n, desc.TryGetValue(n, out d) ? d : ""));
        }
        return list;
    }

    static int PortNo(string name)
    {
        int n;
        return name.Length > 3 && int.TryParse(name.Substring(3), out n) ? n : 9999;
    }
}

static class SerialParse
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex Pair = new Regex(@"([A-Za-z_][A-Za-z0-9_]*)\s*[:=]\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)(?![\dA-Za-z_:])");
    static readonly Regex Plain = new Regex(@"^\s*[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?(\s*[,;\t ]\s*[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)*\s*$");

    // Satirdaki sayisal degerler:
    //   "SET:01500,ACT:01498,T:+0045.3"  ->  SET, ACT, T        (ad:deger ya da ad=deger ciftleri)
    //   "12.5, 3.3, 100"                 ->  S1, S2, S3         (yalnizca sayilardan olusan satir)
    public static List<KeyValuePair<string, double>> Pairs(string line)
    {
        List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
        double v;
        foreach (Match m in Pair.Matches(line))
            if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, Inv, out v))
                list.Add(new KeyValuePair<string, double>(m.Groups[1].Value, v));
        if (list.Count == 0 && Plain.IsMatch(line))
        {
            int i = 1;
            foreach (string tok in line.Split(new char[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(tok, NumberStyles.Float, Inv, out v)) list.Add(new KeyValuePair<string, double>("S" + i++, v));
        }
        return list;
    }

    // Seri kayit dosyasi satiri: "saat <sekme> t <sekme> metin". t okunamazsa NaN.
    public static bool SplitRecord(string record, out double t, out string text)
    {
        t = double.NaN; text = "";
        string[] f = record.Split(new char[] { '\t' }, 3);
        if (f.Length < 3) return false;
        text = f[2];
        return double.TryParse(f[1].Replace(',', '.'), NumberStyles.Float, Inv, out t);
    }
}
