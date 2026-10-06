// ScopeCli.exe - komut satiri araci. Derleme icin: build.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static volatile bool cancel;
    static StreamWriter csv;
    static string dumpDir;
    static uint timeout = 3000;

    static string F(string fmt, params object[] a) { return string.Format(Inv, fmt, a); }

    // Olcum degerleri ve CSV, bilgisayarin bolge ayariyla yazilir (Turkce: ondalik virgul, sutun ayirici ';')
    // ki dosya Excel'de cift tiklayinca dogrudan sutunlara ayrilsin.
    static readonly CultureInfo Cur = CultureInfo.CurrentCulture;
    static readonly string Sep = Cur.TextInfo.ListSeparator;

    // Sayiyi us gosterimi olmadan, 'digits' anlamli basamakla yazar: 1.166E-02 -> 0,01166
    static string Val(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return "";
        if (x == 0) return "0";
        int dec = digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(x)));
        decimal d = dec > 28 ? 0m : Math.Round((decimal)x, Math.Max(dec, 0));
        return d.ToString("0.############################", Cur);
    }

    static string Unit(string p)
    {
        switch (p)
        {
            case "PKPK": case "MAX": case "MIN": case "AMPL": case "TOP": case "BASE": case "MEAN": case "CMEAN":
            case "RMS": case "CRMS": case "STDEV": case "VSTD": return "[V]";
            case "PER": case "PWID": case "NWID": case "RISE": case "FALL": case "WID": case "DELAY": return "[s]";
            case "FREQ": return "[Hz]";
            case "DUTY": case "NDUTY": case "OVSN": case "OVSP": case "RPRE": case "FPRE": return "[%]";
        }
        return "";
    }

    static double Num(string resp)
    {
        int sp = resp.LastIndexOf(' ');
        Match m = Regex.Match(sp >= 0 ? resp.Substring(sp + 1) : resp, @"[-+]?\d+(\.\d+)?([eE][-+]?\d+)?");
        return m.Success ? double.Parse(m.Value, Inv) : double.NaN;
    }

    static bool IsText(byte[] r)
    {
        int lim = Math.Min(r.Length, 4096);
        for (int i = 0; i < lim; i++) if (r[i] != 9 && r[i] != 10 && r[i] != 13 && (r[i] < 32 || r[i] > 126)) return false;
        return true;
    }

    static string Text(byte[] r)
    {
        if (!IsText(r)) return "<" + r.Length + " bayt ikili veri>";
        return Encoding.ASCII.GetString(r).TrimEnd('\r', '\n', ' ');
    }

    // Veri kaynagi: Run() her cevrimde Sample() cagirir, donen degerleri konsola/CSV'ye bir satir olarak yazar.
    // Yeni bir veri turu (ornegin ham dalga sekli kaydi) eklemek icin bu arayuzu uygulayip HandleLine'a komut eklemek yeterli.
    interface ISource
    {
        string[] Start(Usb u, Action<string> say); // sutun adlarini dondurur
        string[] Sample(Usb u);
    }

    sealed class QuerySource : ISource
    {
        readonly string query;
        public QuerySource(string q) { query = q; }
        public string[] Start(Usb u, Action<string> say) { return new string[] { "cevap" }; }
        public string[] Sample(Usb u) { return new string[] { Text(u.Query(query)) }; }
    }

    // Osiloskobun kendi olcumleri (PAVA). Tanim: "C1:RMS,PKPK,PER C2:FREQ"
    sealed class MeasSource : ISource
    {
        sealed class Group { public string Ch; public List<string> Params = new List<string>(); }
        static readonly string[] Default = { "PKPK", "RMS", "MEAN", "FREQ", "PER" };
        // Tek tek sormak yerine PAVA? ALL kullanilacak en az parametre sayisi (ALL tum degerleri ayni yakalamadan verir)
        const int AllThreshold = 2;
        readonly List<Group> groups = new List<Group>();

        static string Alias(string p)
        {
            switch (p)
            {
                case "VPP": case "PP": return "PKPK";
                case "PRD": case "PERIOD": return "PER";
                case "VRMS": return "RMS";
                case "VMAX": return "MAX";
                case "VMIN": return "MIN";
                case "VAMP": case "AMP": return "AMPL";
                case "VMEAN": case "AVG": return "MEAN";
                case "FRQ": return "FREQ";
            }
            return p;
        }

        public MeasSource(string spec)
        {
            char[] sep = { ' ', ',' };
            foreach (string part in spec.ToUpperInvariant().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string pars = part;
                int colon = part.IndexOf(':');
                if (colon >= 0 || Regex.IsMatch(part, @"^C\d$"))
                {
                    Group g = new Group();
                    g.Ch = colon >= 0 ? part.Substring(0, colon) : part;
                    pars = colon >= 0 ? part.Substring(colon + 1) : "";
                    groups.Add(g);
                }
                else if (groups.Count == 0) { Group g = new Group(); g.Ch = "C1"; groups.Add(g); }
                foreach (string p in pars.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                    groups[groups.Count - 1].Params.Add(Alias(p));
            }
            if (groups.Count == 0) { Group g = new Group(); g.Ch = "C1"; groups.Add(g); }
            foreach (Group g in groups) if (g.Params.Count == 0) g.Params.AddRange(Default);
        }

        public string[] Start(Usb u, Action<string> say)
        {
            List<string> cols = new List<string>();
            foreach (Group g in groups) foreach (string p in g.Params) cols.Add(g.Ch + "_" + p + Unit(p));
            return cols.ToArray();
        }

        // "****" (gecersiz olcum) ve birim eklerini ayiklar; gecersizse bos doner
        static string Value(string s)
        {
            Match m = Regex.Match(s, @"^\s*[-+]?\d+(\.\d+)?([eE][-+]?\d+)?");
            return m.Success ? Val(double.Parse(m.Value, Inv), 4) : "";
        }

        public string[] Sample(Usb u)
        {
            List<string> vals = new List<string>();
            foreach (Group g in groups)
            {
                bool all = g.Params.Count >= AllThreshold;
                Dictionary<string, string> d = new Dictionary<string, string>();
                foreach (string q in all ? new string[] { "ALL" } : g.Params.ToArray())
                {
                    string r = u.QueryText(g.Ch + ":PAVA? " + q);
                    string[] tok = r.Substring(r.LastIndexOf(' ') + 1).Split(',');
                    for (int i = 0; i + 1 < tok.Length; i += 2) d[tok[i]] = tok[i + 1];
                }
                foreach (string p in g.Params)
                {
                    string v;
                    vals.Add(d.TryGetValue(p, out v) ? Value(v) : "");
                }
            }
            return vals.ToArray();
        }
    }

    // Ham dalga sekli: kare basina ozet verir, --dump verilmisse her kareyi ayri CSV'ye yazar.
    sealed class WaveSource : ISource
    {
        readonly string ch;
        double vdiv, ofst, sara;
        long frame;
        public WaveSource(string channel) { ch = channel; }

        public string[] Start(Usb u, Action<string> say)
        {
            vdiv = Num(u.QueryText(ch + ":VDIV?"));
            ofst = Num(u.QueryText(ch + ":OFST?"));
            sara = Num(u.QueryText("SARA?"));
            say(F("# {0}: {1} V/div, ofset {2} V, {3} Sa/s", ch, vdiv, ofst, sara));
            return new string[] { "nokta", "min[V]", "max[V]", "ort[V]" };
        }

        public string[] Sample(Usb u)
        {
            byte[] r = u.Query(ch + ":WF? DAT2");
            int hash = Array.IndexOf(r, (byte)'#');
            if (hash < 0 || hash + 2 > r.Length) throw new IOException("beklenmeyen cevap: " + Text(r));
            int nd = r[hash + 1] - '0';
            int len = int.Parse(Encoding.ASCII.GetString(r, hash + 2, nd), Inv);
            int start = hash + 2 + nd;
            len = Math.Min(len, r.Length - start);
            int lo = 127, hi = -128; long sum = 0;
            for (int i = 0; i < len; i++)
            {
                int c = (sbyte)r[start + i];
                if (c < lo) lo = c;
                if (c > hi) hi = c;
                sum += c;
            }
            double k = vdiv / 25.0;
            frame++;
            if (dumpDir != null)
            {
                using (StreamWriter w = new StreamWriter(Path.Combine(dumpDir, F("{0}_{1:D6}.csv", ch, frame))))
                {
                    w.WriteLine("t,v");
                    for (int i = 0; i < len; i++)
                        w.WriteLine(F("{0:G9},{1:G5}", i / sara, (sbyte)r[start + i] * k - ofst));
                }
            }
            return new string[] { len.ToString(Inv), Val(lo * k - ofst, 5), Val(hi * k - ofst, 5),
                Val(len > 0 ? (double)sum / len * k - ofst : double.NaN, 5) };
        }
    }

    static void Run(Usb u, ISource src, int ms, Action<string> say, Func<bool> stop)
    {
        // Sorgular arka arkaya gelirse SDS1104X-E yeni yakalama yapamiyor: ekran donuyor ve hep ayni deger donuyor.
        // Olcumle bulunan esik ~30 ms; altina inilmesin.
        ms = Math.Max(ms, 40);
        string[] cols = src.Start(u, say);
        say("# t[s]\t" + string.Join("\t", cols));
        if (csv != null) csv.WriteLine("zaman" + Sep + "t[s]" + Sep + string.Join(Sep, cols));
        Stopwatch sw = Stopwatch.StartNew();
        long n = 0;
        int errors = 0;
        while (!stop())
        {
            string[] v;
            try { v = src.Sample(u); errors = 0; }
            catch (Exception e)
            {
                // Uzun testte tek bir iletisim hatasi kaydi bitirmesin: temizle, not dus, devam et
                if (!(e is IOException || e is TimeoutException) || ++errors >= 5) throw;
                u.Clear();
                u.SetTimeout(timeout);
                say(F("# {0:F4} HATA: {1} (yeniden deneniyor)", sw.Elapsed.TotalSeconds, e.Message));
                continue;
            }
            string t = sw.Elapsed.TotalSeconds.ToString("F2", Cur);
            say(t + "\t" + string.Join("\t", v));
            if (csv != null) csv.WriteLine(DateTime.Now.ToString("G", Cur) + Sep + t + Sep + string.Join(Sep, v));
            n++;
            if (ms > 0) Thread.Sleep(ms);
        }
        say(F("# {0} ornek, {1:F1} ornek/s", n, n / sw.Elapsed.TotalSeconds));
    }

    // Argumanin basindaki istege bagli bekleme suresini (ms) ayirir
    static int SplitInterval(ref string arg)
    {
        int ms;
        string[] q = arg.Split(new char[] { ' ' }, 2);
        if (!int.TryParse(q[0], out ms)) return 0;
        arg = q.Length > 1 ? q[1].Trim() : "";
        return ms;
    }

    const string Help =
        "Dogrudan SCPI yazin (icinde ? varsa cevap okunur). Ozel komutlar:\r\n" +
        "  !meas [ms] <tanim>   olcumleri durmadan oku (ornek: !meas C1:RMS,PKPK,PER C2:FREQ)\r\n" +
        "  !loop [ms] <sorgu>   herhangi bir sorguyu durmadan tekrarla (ornek: !loop C1:PAVA? MEAN)\r\n" +
        "  !wave <kanal>        dalga seklini durmadan cek, kare basina min/max/ort (ornek: !wave C1)\r\n" +
        "  !clear               USB tamponlarini temizle\r\n" +
        "  !help                bu yardim\r\n" +
        "Dongu, yeni bir satir/tus gelince durur.";

    static void HandleLine(Usb u, string line, Action<string> say, Action<byte[]> reply, Func<bool> stop)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        try
        {
            if (line[0] != '!')
            {
                u.Write(line);
                // SCDP (ekran goruntusu) soru isareti olmadan cevap donduren tek komut
                if (line.IndexOf('?') >= 0 || line.ToUpperInvariant() == "SCDP") reply(u.Read());
                return;
            }
            string[] p = line.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string arg = p.Length > 1 ? p[1].Trim() : "";
            switch (p[0].ToLowerInvariant())
            {
                case "!help": say(Help); break;
                case "!clear": u.Clear(); say("# temizlendi"); break;
                case "!loop":
                    {
                        int ms = SplitInterval(ref arg);
                        if (arg.Length == 0) { say("# kullanim: !loop [ms] <sorgu>"); break; }
                        Run(u, new QuerySource(arg), ms, say, stop);
                        break;
                    }
                case "!meas":
                    {
                        int ms = SplitInterval(ref arg);
                        Run(u, new MeasSource(arg), ms, say, stop);
                        break;
                    }
                case "!wave":
                    {
                        int ms = SplitInterval(ref arg);
                        Run(u, new WaveSource(arg.Length == 0 ? "C1" : arg.ToUpperInvariant()), ms, say, stop);
                        break;
                    }
                default: say("# bilinmeyen komut, !help yazin"); break;
            }
        }
        catch (TimeoutException)
        {
            u.Clear();
            u.SetTimeout(timeout);
            say("# ZAMAN ASIMI (cihaz cevap vermedi)");
        }
        catch (IOException e)
        {
            u.Clear();
            u.SetTimeout(timeout);
            say("# HATA: " + e.Message);
        }
    }

    static bool ConsoleStop()
    {
        if (cancel) return true;
        try { if (Console.KeyAvailable) { Console.ReadKey(true); return true; } } catch { }
        return false;
    }

    static void ConsoleLine(Usb u, string line)
    {
        cancel = false;
        HandleLine(u, line, Console.WriteLine, delegate(byte[] r) { Console.WriteLine(Text(r)); }, ConsoleStop);
    }

    // Seri port ya da TCP ucundan gelen satirlari osiloskopa aktarir (Termite vb. icin).
    static void Bridge(Usb u, Func<int> readByte, Func<bool> pending, Action<byte[]> write)
    {
        Action<string> say = delegate(string s) { write(Encoding.ASCII.GetBytes(s + "\r\n")); };
        StringBuilder sb = new StringBuilder();
        for (; ; )
        {
            int c = readByte();
            if (c < 0) return;
            if (c != '\r' && c != '\n') { sb.Append((char)c); continue; }
            string line = sb.ToString();
            sb.Length = 0;
            bool skipLf = c == '\r'; // CR+LF gonderen terminalde LF donguyu durdurmasin
            Func<bool> stop = delegate
            {
                while (pending())
                {
                    int b = readByte();
                    if (b == '\n' && skipLf) { skipLf = false; continue; }
                    if (b >= 0 && b != '\r' && b != '\n') sb.Append((char)b);
                    return true;
                }
                return false;
            };
            HandleLine(u, line, say, delegate(byte[] r) { if (IsText(r)) say(Text(r)); else write(r); }, stop);
        }
    }

    static int Usage()
    {
        Console.WriteLine(
            "ScopeCli.exe                         etkilesimli SCPI terminali\r\n" +
            "ScopeCli.exe \"*IDN?\"                 tek komut/sorgu\r\n" +
            "ScopeCli.exe --meas C1:RMS,PKPK,PER  olcumleri durmadan oku (Ctrl+C ile dur); cok kanal: C1:RMS C2:FREQ\r\n" +
            "ScopeCli.exe --loop \"C1:PAVA? MEAN\"  herhangi bir sorguyu durmadan tekrarla\r\n" +
            "ScopeCli.exe --wave C1               dalga seklini durmadan cek\r\n" +
            "ScopeCli.exe --com COM11             seri port koprusu (Termite icin, com0com cifti gerekir)\r\n" +
            "ScopeCli.exe --tcp 5025              TCP koprusu (127.0.0.1)\r\n" +
            "Secenekler: --interval <ms>  --csv <dosya>  --dump <klasor> (her kareyi CSV yaz)  --timeout <ms>\r\n");
        return 1;
    }

    static int Main(string[] args)
    {
        string mode = "", target = "";
        int interval = 0;
        string csvPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool more = i + 1 < args.Length;
            if (a == "--meas") { mode = a; target = ""; while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) target += " " + args[++i]; }
            else if ((a == "--loop" || a == "--wave" || a == "--com" || a == "--tcp") && more) { mode = a; target = args[++i]; }
            else if (a == "--interval" && more) interval = int.Parse(args[++i], Inv);
            else if (a == "--timeout" && more) timeout = uint.Parse(args[++i], Inv);
            else if (a == "--csv" && more) csvPath = args[++i];
            else if (a == "--dump" && more) { dumpDir = args[++i]; Directory.CreateDirectory(dumpDir); }
            else if (a.StartsWith("--") || a == "/?") return Usage();
            else { mode = "once"; target = a; }
        }

        using (Usb u = Usb.Open(timeout))
        {
            if (u == null)
            {
                Console.Error.WriteLine("Siglent cihazi acilamadi (bagli mi, WinUSB surucusu kurulu mu, baska program kullaniyor mu?)");
                return 2;
            }
            u.Clear();
            u.SetTimeout(timeout);
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { if (!cancel) { cancel = true; e.Cancel = true; } };
            if (csvPath != null) { csv = new StreamWriter(csvPath); csv.AutoFlush = true; }
            try
            {
                switch (mode)
                {
                    case "once": ConsoleLine(u, target); break;
                    case "--loop": ConsoleLine(u, "!loop " + interval + " " + target); break;
                    case "--meas": ConsoleLine(u, "!meas " + interval + target); break;
                    case "--wave": ConsoleLine(u, "!wave " + interval + " " + target); break;
                    case "--com":
                        using (SerialPort sp = new SerialPort(target, 115200))
                        {
                            sp.Open();
                            Console.WriteLine("Kopru: " + target + " <-> " + u.QueryText("*IDN?"));
                            Stream st = sp.BaseStream;
                            Bridge(u, st.ReadByte, delegate { return sp.BytesToRead > 0; },
                                delegate(byte[] b) { st.Write(b, 0, b.Length); });
                        }
                        break;
                    case "--tcp":
                        {
                            TcpListener l = new TcpListener(IPAddress.Loopback, int.Parse(target, Inv));
                            l.Start();
                            Console.WriteLine("Kopru: 127.0.0.1:" + target + " <-> " + u.QueryText("*IDN?"));
                            for (; ; )
                                using (TcpClient c = l.AcceptTcpClient())
                                {
                                    NetworkStream st = c.GetStream();
                                    try
                                    {
                                        Bridge(u, st.ReadByte, delegate { return st.DataAvailable; },
                                            delegate(byte[] b) { st.Write(b, 0, b.Length); });
                                    }
                                    catch (IOException) { }
                                }
                        }
                    default:
                        Console.WriteLine(u.QueryText("*IDN?"));
                        Console.WriteLine(Help);
                        for (; ; )
                        {
                            Console.Write("> ");
                            string line = Console.ReadLine();
                            if (line == null || line == "exit" || line == "quit") break;
                            ConsoleLine(u, line);
                        }
                        break;
                }
            }
            finally { if (csv != null) csv.Close(); }
        }
        return 0;
    }
}
