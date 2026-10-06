// Baglanti arayuzu (USB / ag) ve marka bazli olcum komut setleri.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

// Cihazla konusan her baglanti turu bunu uygular (WinUSB: usb.cs icindeki Usb, ag: TcpLink)
interface ILink : IDisposable
{
    void Write(string cmd);
    byte[] Read();
    byte[] Query(string cmd);
    string QueryText(string cmd);
    void Clear();
    void SetTimeout(uint ms);
}

// Ag uzerinden ham SCPI soketi (cogu cihazda port 5025, Rigol'de 5555)
class TcpLink : ILink
{
    TcpClient client;
    NetworkStream s;

    public static TcpLink Open(string address, uint timeoutMs)
    {
        string host = address.Trim();
        int port = 5025;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host.Substring(colon + 1), out port)) host = host.Substring(0, colon); else port = 5025;
        if (host.Length == 0) return null;
        TcpClient c = new TcpClient();
        try
        {
            IAsyncResult ar = c.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(2000)) { c.Close(); return null; }
            c.EndConnect(ar);
        }
        catch (SocketException) { c.Close(); return null; }
        c.NoDelay = true;
        TcpLink l = new TcpLink();
        l.client = c; l.s = c.GetStream();
        l.SetTimeout(timeoutMs);
        return l;
    }

    public void SetTimeout(uint ms) { s.ReadTimeout = (int)ms; s.WriteTimeout = (int)ms; }

    public void Write(string cmd)
    {
        byte[] b = Encoding.ASCII.GetBytes(cmd + "\n");
        s.Write(b, 0, b.Length);
    }

    int Next()
    {
        int b;
        try { b = s.ReadByte(); }
        catch (IOException e)
        {
            SocketException se = e.InnerException as SocketException;
            if (se != null && se.SocketErrorCode == SocketError.TimedOut) throw new TimeoutException("okuma: zaman asimi");
            throw;
        }
        if (b < 0) throw new IOException("baglanti kapandi");
        return b;
    }

    void ReadExact(MemoryStream ms, int n) { for (int i = 0; i < n; i++) ms.WriteByte((byte)Next()); }

    public byte[] Read()
    {
        MemoryStream ms = new MemoryStream();
        int b = Next();
        while (b == '\r' || b == '\n') b = Next(); // onceki cevabin satir sonu
        ms.WriteByte((byte)b);
        if (b == '#')
        {
            // IEEE 488.2 blok: #<n><n basamak uzunluk><veri>
            int nd = Next(); ms.WriteByte((byte)nd);
            nd -= '0';
            if (nd > 0 && nd <= 9)
            {
                int len = 0;
                for (int i = 0; i < nd; i++) { int d = Next(); ms.WriteByte((byte)d); len = len * 10 + (d - '0'); }
                ReadExact(ms, len);
                return ms.ToArray();
            }
        }
        else if (b == 'B')
        {
            // Siglent ekran goruntusu basliksiz ham BMP dondurur; boyut dosya basliginda
            int b2 = Next(); ms.WriteByte((byte)b2);
            if (b2 == 'M')
            {
                ReadExact(ms, 4);
                int size = BitConverter.ToInt32(ms.GetBuffer(), 2);
                if (size > 100000 && size < 64000000) { ReadExact(ms, size - 6); return ms.ToArray(); }
            }
            if (b2 == '\n') return ms.ToArray();
        }
        for (; ; )
        {
            b = Next();
            ms.WriteByte((byte)b);
            if (b == '\n') return ms.ToArray();
        }
    }

    public byte[] Query(string cmd) { Write(cmd); return Read(); }

    public string QueryText(string cmd) { return Encoding.ASCII.GetString(Query(cmd)).TrimEnd('\r', '\n', '\0', ' '); }

    public void Clear()
    {
        byte[] buf = new byte[65536];
        while (s.DataAvailable) s.Read(buf, 0, buf.Length);
    }

    public void Dispose() { if (client != null) { client.Close(); client = null; } }
}

// Bir markanin olcum komut seti. Parametreler uygulamanin ortak kodlariyla istenir (RMS, MEAN, PKPK, ...).
abstract class Dialect
{
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public string Name;
    public string ScreenshotCmd, ScreenshotExt; // null = desteklenmiyor

    // Bir kanalin istenen parametrelerini okur; desteklenmeyen ya da gecersiz olcum NaN doner
    public abstract double[] Measure(ILink u, int channel, IList<string> codes);

    // Cevaptaki son sayi; 9.9E37 cogu markada "olcum gecersiz" demek
    public static double Num(string s)
    {
        MatchCollection m = Regex.Matches(s, @"[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?");
        if (m.Count == 0) return double.NaN;
        double v;
        if (!double.TryParse(m[m.Count - 1].Value, NumberStyles.Float, Inv, out v) || Math.Abs(v) > 9e36) return double.NaN;
        return v;
    }

    public static readonly string[] Names = { "Otomatik", "Siglent", "Rigol", "Keysight / Agilent", "Tektronix" };

    // index: Names icindeki sira; 0 ise *IDN? cevabindaki ureticiye gore secilir
    public static Dialect Pick(int index, string idn)
    {
        string id = idn.ToUpperInvariant();
        if (index == 0)
        {
            if (id.Contains("SIGLENT")) index = 1;
            else if (id.Contains("RIGOL")) index = 2;
            else if (id.Contains("KEYSIGHT") || id.Contains("AGILENT")) index = 3;
            else if (id.Contains("TEKTRONIX")) index = 4;
            else
            {
                // Taninmayan marka: en yaygin SCPI bicimi denenir
                Dialect g = Keysight();
                g.Name = "Genel SCPI (tanınmayan marka)";
                return g;
            }
        }
        switch (index)
        {
            case 2: return Rigol();
            case 3: return Keysight();
            case 4: return Tektronix();
        }
        return new SiglentDialect();
    }

    static Dialect Rigol()
    {
        TemplateDialect d = new TemplateDialect("Rigol", ":MEASure:ITEM? {0},CHANnel{1}");
        d.Map("RMS", "VRMS").Map("MEAN", "VAVG").Map("PKPK", "VPP").Map("MAX", "VMAX").Map("MIN", "VMIN").Map("AMPL", "VAMP")
         .Map("TOP", "VTOP").Map("BASE", "VBASe").Map("CRMS", "PVRMS").Map("FREQ", "FREQuency").Map("PER", "PERiod")
         .Map("DUTY", "PDUTy", 100) // Rigol orani 0-1 arasi verir
         .Map("PWID", "PWIDth").Map("NWID", "NWIDth").Map("RISE", "RTIMe").Map("FALL", "FTIMe");
        return d;
    }

    static Dialect Keysight()
    {
        TemplateDialect d = new TemplateDialect("Keysight / Agilent", ":MEASure:{0}? CHANnel{1}");
        d.Map("RMS", "VRMS").Map("MEAN", "VAVerage").Map("PKPK", "VPP").Map("MAX", "VMAX").Map("MIN", "VMIN").Map("AMPL", "VAMPlitude")
         .Map("TOP", "VTOP").Map("BASE", "VBASe").Map("FREQ", "FREQuency").Map("PER", "PERiod").Map("DUTY", "DUTYcycle")
         .Map("PWID", "PWIDth").Map("NWID", "NWIDth").Map("RISE", "RISetime").Map("FALL", "FALLtime");
        return d;
    }

    static Dialect Tektronix()
    {
        TemplateDialect d = new TemplateDialect("Tektronix",
            "MEASUrement:IMMed:SOUrce1 CH{1};:MEASUrement:IMMed:TYPe {0};:MEASUrement:IMMed:VALue?");
        d.Map("RMS", "RMS").Map("MEAN", "MEAN").Map("PKPK", "PK2pk").Map("MAX", "MAXImum").Map("MIN", "MINImum").Map("AMPL", "AMPlitude")
         .Map("TOP", "HIGH").Map("BASE", "LOW").Map("CRMS", "CRMs").Map("CMEAN", "CMEan").Map("FREQ", "FREQuency").Map("PER", "PERIod")
         .Map("DUTY", "PDUty").Map("PWID", "PWIdth").Map("NWID", "NWIdth").Map("RISE", "RISe").Map("FALL", "FALL");
        return d;
    }
}

// Siglent (PAVA): tek sorguda tum olcumleri verebilir
class SiglentDialect : Dialect
{
    public SiglentDialect() { Name = "Siglent"; ScreenshotCmd = "SCDP"; ScreenshotExt = ".bmp"; }

    public override double[] Measure(ILink u, int channel, IList<string> codes)
    {
        // Tek parametrede dogrudan sor; birden fazlaysa ALL ile hepsi ayni yakalamadan gelir
        string r = u.QueryText("C" + channel + ":PAVA? " + (codes.Count == 1 ? codes[0] : "ALL"));
        string[] tok = r.Substring(r.LastIndexOf(' ') + 1).Split(',');
        Dictionary<string, string> d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < tok.Length; i += 2) d[tok[i]] = tok[i + 1];
        double[] v = new double[codes.Count];
        for (int i = 0; i < codes.Count; i++)
        {
            string s;
            v[i] = d.TryGetValue(codes[i], out s) && !s.Contains("*") ? Num(s) : double.NaN; // "****" = gecersiz
        }
        return v;
    }
}

// Her parametre icin ayri sorgu gonderen markalar: komut sablonu + parametre adlari
class TemplateDialect : Dialect
{
    readonly string format; // {0} = olcum adi, {1} = kanal numarasi
    readonly Dictionary<string, string> items = new Dictionary<string, string>();
    readonly Dictionary<string, double> scale = new Dictionary<string, double>();

    public TemplateDialect(string name, string format) { Name = name; this.format = format; }

    public TemplateDialect Map(string code, string item) { items[code] = item; return this; }
    public TemplateDialect Map(string code, string item, double factor) { items[code] = item; scale[code] = factor; return this; }

    public override double[] Measure(ILink u, int channel, IList<string> codes)
    {
        double[] v = new double[codes.Count];
        for (int i = 0; i < codes.Count; i++)
        {
            string item; double k;
            if (!items.TryGetValue(codes[i], out item)) { v[i] = double.NaN; continue; }
            v[i] = Num(u.QueryText(string.Format(Inv, format, item, channel)));
            if (scale.TryGetValue(codes[i], out k)) v[i] *= k;
        }
        return v;
    }
}
