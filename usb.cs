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

class Usb : ILink
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct IfDesc { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }
    [StructLayout(LayoutKind.Sequential)]
    struct PipeInfo { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Setup { public byte RequestType, Request; public ushort Value, Index, Length; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle f, out IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Free(IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_QueryInterfaceSettings(IntPtr h, byte alt, out IfDesc d);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_QueryPipe(IntPtr h, byte alt, byte idx, out PipeInfo p);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr h, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint n, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint n, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ResetPipe(IntPtr h, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ControlTransfer(IntPtr h, Setup s, byte[] buf, uint len, out uint n, IntPtr ov);

    SafeFileHandle file;
    IntPtr h;
    byte epIn, epOut, ifNum, tag;
    readonly byte[] rbuf = new byte[1 << 20];
    public string DevicePath;
    public static bool Debug = Environment.GetEnvironmentVariable("SDS_DEBUG") == "1";

    static IEnumerable<string> Candidates()
    {
        const string generic = "{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
        List<string> list = new List<string>();
        try
        {
            using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                foreach (string id in usb.GetSubKeyNames())
                {
                    using (RegistryKey dev = usb.OpenSubKey(id))
                        foreach (string serial in dev.GetSubKeyNames())
                        {
                            // Uretici ayirt etmeden WinUSB surucusune bagli tum cihazlar aday; USBTMC olup olmadigina Open bakar
                            try
                            {
                                using (RegistryKey inst = dev.OpenSubKey(serial))
                                    if (!"WinUSB".Equals(inst.GetValue("Service") as string, StringComparison.OrdinalIgnoreCase)) continue;
                            }
                            catch { continue; }
                            string prefix = @"\\?\usb#" + id.ToLowerInvariant() + "#" + serial.ToLowerInvariant() + "#";
                            try
                            {
                                using (RegistryKey par = dev.OpenSubKey(serial + @"\Device Parameters"))
                                {
                                    object v = par.GetValue("DeviceInterfaceGUIDs") ?? par.GetValue("DeviceInterfaceGUID");
                                    string[] guids = v as string[] ?? (v is string ? new string[] { (string)v } : new string[0]);
                                    foreach (string g in guids) list.Add(prefix + g.ToLowerInvariant());
                                }
                            }
                            catch { }
                            list.Add(prefix + generic);
                        }
                }
        }
        catch { }
        return list;
    }

    public static Usb Open(uint timeoutMs)
    {
        foreach (string p in Candidates())
        {
            SafeFileHandle f = CreateFile(p, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (f.IsInvalid) continue;
            IntPtr ih;
            if (!WinUsb_Initialize(f, out ih)) { f.Close(); continue; }
            Usb u = new Usb();
            u.file = f; u.h = ih; u.DevicePath = p;
            u.tag = (byte)new Random().Next(1, 255); // onceki calistirmanin etiketleriyle cakismasin
            IfDesc d;
            // USB Test & Measurement sinifi (0xFE / 0x03) degilse olcum cihazi degildir
            if (!WinUsb_QueryInterfaceSettings(ih, 0, out d) || d.bInterfaceClass != 0xFE || d.bInterfaceSubClass != 3) { u.Dispose(); continue; }
            u.ifNum = d.bInterfaceNumber;
            for (byte i = 0; i < d.bNumEndpoints; i++)
            {
                PipeInfo pi;
                if (!WinUsb_QueryPipe(ih, 0, i, out pi) || pi.PipeType != 2) continue; // 2 = bulk
                if ((pi.PipeId & 0x80) != 0) u.epIn = pi.PipeId; else u.epOut = pi.PipeId;
            }
            if (u.epIn == 0 || u.epOut == 0) { u.Dispose(); continue; }
            u.SetTimeout(timeoutMs);
            return u;
        }
        return null;
    }

    public void SetTimeout(uint ms)
    {
        WinUsb_SetPipePolicy(h, epIn, 3, 4, ref ms);  // PIPE_TRANSFER_TIMEOUT
        WinUsb_SetPipePolicy(h, epOut, 3, 4, ref ms);
    }

    static Exception Fail(string what)
    {
        int e = Marshal.GetLastWin32Error();
        if (e == 121) return new TimeoutException(what + ": zaman asimi");
        return new IOException(what + ": Win32 hata " + e);
    }

    byte NextTag() { tag = (byte)(tag == 255 ? 1 : tag + 1); return tag; }

    int ReadPipe()
    {
        uint n;
        if (!WinUsb_ReadPipe(h, epIn, rbuf, (uint)rbuf.Length, out n, IntPtr.Zero)) throw Fail("okuma");
        return (int)n;
    }

    public void Write(byte[] data)
    {
        byte[] b = new byte[(12 + data.Length + 3) & ~3];
        byte t = NextTag();
        b[0] = 1; b[1] = t; b[2] = (byte)~t;
        BitConverter.GetBytes(data.Length).CopyTo(b, 4);
        b[8] = 1; // EOM
        data.CopyTo(b, 12);
        uint n;
        if (!WinUsb_WritePipe(h, epOut, b, (uint)b.Length, out n, IntPtr.Zero)) throw Fail("yazma");
    }

    public void Write(string cmd) { Write(Encoding.ASCII.GetBytes(cmd + "\n")); }

    public byte[] Read()
    {
        MemoryStream ms = new MemoryStream();
        for (; ; )
        {
            byte[] rq = new byte[12];
            byte t = NextTag();
            rq[0] = 2; rq[1] = t; rq[2] = (byte)~t;
            BitConverter.GetBytes(rbuf.Length - 12).CopyTo(rq, 4);
            uint w;
            if (!WinUsb_WritePipe(h, epOut, rq, 12, out w, IntPtr.Zero)) throw Fail("yazma");
            int n, size, got;
            bool eom;
            for (int stale = 0; ; stale++)
            {
                n = ReadPipe();
                if (Debug)
                    Console.Error.WriteLine("  [rx n={0} istenen tag={1} hdr={2} veri='{3}']", n, t, BitConverter.ToString(rbuf, 0, Math.Min(n, 12)),
                        n > 12 ? Encoding.ASCII.GetString(rbuf, 12, Math.Min(n - 12, 60)).Replace("\n", "\\n") : "");
                if (n < 12 || rbuf[0] != 2) throw new IOException("USBTMC senkron hatasi");
                size = BitConverter.ToInt32(rbuf, 4);
                eom = (rbuf[8] & 1) != 0;
                got = Math.Min(n - 12, size);
                if (rbuf[1] == t) break;
                // Etiket tutmuyor: yarim kalmis onceki bir istegin cevabi. At, kendi cevabimiz arkasindan gelir.
                if (stale >= 8) throw new IOException("USBTMC senkron hatasi");
                while (got < size)
                {
                    n = ReadPipe();
                    if (n == 0) throw new IOException("USBTMC eksik veri");
                    got += Math.Min(n, size - got);
                }
            }
            ms.Write(rbuf, 12, got);
            while (got < size)
            {
                n = ReadPipe();
                if (n == 0) throw new IOException("USBTMC eksik veri");
                int take = Math.Min(n, size - got);
                ms.Write(rbuf, 0, take);
                got += take;
            }
            if (eom) break;
        }
        return ms.ToArray();
    }

    public byte[] Query(string cmd) { Write(cmd); return Read(); }

    public string QueryText(string cmd) { return Encoding.ASCII.GetString(Query(cmd)).TrimEnd('\r', '\n', '\0', ' '); }

    // USBTMC INITIATE_CLEAR: yarim kalmis bir aktarimdan sonra cihazin giris/cikis tamponlarini bosaltir.
    public void Clear()
    {
        byte[] b = new byte[2];
        uint n;
        // Yarim kalmis bir calistirmadan giris ucunda bekleyen veri varsa oku ve at
        // (bu cihazda INITIATE_CLEAR tek basina bunu temizlemiyor)
        uint drainTo = 100;
        WinUsb_SetPipePolicy(h, epIn, 3, 4, ref drainTo);
        for (int i = 0; i < 64 && WinUsb_ReadPipe(h, epIn, rbuf, (uint)rbuf.Length, out n, IntPtr.Zero); i++) { }
        Setup s = new Setup();
        s.RequestType = 0xA1; s.Request = 5; s.Index = ifNum; s.Length = 1;
        WinUsb_ControlTransfer(h, s, b, 1, out n, IntPtr.Zero);
        s.Request = 6; s.Length = 2;
        uint shortTo = 100;
        WinUsb_SetPipePolicy(h, epIn, 3, 4, ref shortTo);
        for (int i = 0; i < 50; i++)
        {
            if (!WinUsb_ControlTransfer(h, s, b, 2, out n, IntPtr.Zero) || b[0] != 2) break; // 2 = PENDING
            if ((b[1] & 1) != 0) WinUsb_ReadPipe(h, epIn, rbuf, (uint)rbuf.Length, out n, IntPtr.Zero);
            else Thread.Sleep(10);
        }
        WinUsb_ResetPipe(h, epOut);
        WinUsb_ResetPipe(h, epIn);
    }

    public void Dispose()
    {
        if (h != IntPtr.Zero) { WinUsb_Free(h); h = IntPtr.Zero; }
        if (file != null) file.Close();
    }
}

