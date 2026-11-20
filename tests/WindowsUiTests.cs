using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

class WindowsUiTests
{
    class TestForm : MainForm
    {
        protected override void OnShown(EventArgs e) { }
    }

    static object Field(MainForm f, string name)
    {
        return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f);
    }

    static object Call(MainForm f, string name, params object[] args)
    {
        Type[] types = Array.ConvertAll(args, delegate(object a) { return a.GetType(); });
        return typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, types, null).Invoke(f, args);
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Pump()
    {
        Application.DoEvents();
        Thread.Sleep(30);
        Application.DoEvents();
    }

    static void LayoutTest(int width, bool dark)
    {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ayarlar.ini"),
            "tema=" + (dark ? "koyu" : "acik") + "\nseri_panel=1\n");
        Ui.Th = dark ? Theme.MakeDark() : Theme.Light();
        using (TestForm f = new TestForm())
        {
            f.ClientSize = new Size(width, Math.Min(760, Screen.PrimaryScreen.WorkingArea.Height - 80));
            f.Show(); Pump();
            Check(f.ClientSize.Width == width, "Windows constrained the requested test width");
            Panel top = (Panel)Field(f, "topBar");
            foreach (Control a in top.Controls)
            {
                Check(top.ClientRectangle.Contains(a.Bounds), "Clipped top control: " + a.Text + " at width " + width);
                foreach (Control b in top.Controls)
                    if (a != b) Check(!a.Bounds.IntersectsWith(b.Bounds), "Overlapping controls: " + a.Text + " / " + b.Text);
            }
            Button toggle = (Button)Field(f, "btnSerToggle");
            Panel serial = (Panel)Field(f, "serPanel");
            bool wasVisible = serial.Visible;
            toggle.PerformClick(); Pump();
            Check(serial.Visible != wasVisible, "Serial panel toggle failed");
            toggle.PerformClick(); Pump();
            ComboBox chartChoice = (ComboBox)Field(f, "cmbChart");
            Control bar = chartChoice.Parent;
            foreach (Control c in bar.Controls)
                if (c.Visible) Check(c.Bottom <= bar.ClientSize.Height,
                    "Chart toolbar control clipped: " + c.Text + " bottom=" + c.Bottom + " clientHeight=" + bar.ClientSize.Height);
            using (Bitmap image = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "windows-" + width + (dark ? "-dark" : "-light") + ".png"));
            }
            f.Close();
        }
        Console.WriteLine("PASS layout " + width + (dark ? " dark" : " light"));
    }

    static void MeasurementTest()
    {
        TcpListener server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        TcpClient connection = null;
        Thread peer = new Thread(delegate()
        {
            try
            {
                connection = server.AcceptTcpClient();
                using (connection)
                using (StreamReader reader = new StreamReader(connection.GetStream()))
                using (StreamWriter writer = new StreamWriter(connection.GetStream()))
                {
                    writer.AutoFlush = true;
                    string command;
                    while ((command = reader.ReadLine()) != null)
                    {
                        if (command == "*IDN?") writer.WriteLine("Siglent Technologies,SDS1104X-E,WINDOWS-TEST,1.0");
                        else if (command.Contains("PAVA?")) writer.WriteLine("C1:PAVA RMS,0.008,PKPK,0.016");
                        else writer.WriteLine("1");
                    }
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
        peer.IsBackground = true; peer.Start();
        try
        {
            using (TestForm f = new TestForm())
            {
                f.Show(); Pump();
                ((ComboBox)Field(f, "cmbConn")).SelectedIndex = 1;
                ((TextBox)Field(f, "txtAddr")).Text = "127.0.0.1:" + port;
                ((CheckBox)Field(f, "chkRecord")).Checked = false;
                Call(f, "StartMeasure");
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while ((long)Field(f, "samples") < 3 && DateTime.UtcNow < deadline) Pump();
                Check((long)Field(f, "samples") >= 3, "Windows measurement received no samples");
                Call(f, "ShiftChart", -0.25);
                Call(f, "ShowAllTime"); Call(f, "AutoY"); Call(f, "FollowLive");
                Call(f, "EndTest", false);
                Check(!(bool)Field(f, "running"), "Measurement did not stop");
                f.Close();
            }
        }
        finally
        {
            if (connection != null) connection.Close();
            server.Stop(); peer.Join(2000);
        }
        Console.WriteLine("PASS Windows LAN measurement, chart navigation and stop");
    }

    [STAThread]
    static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (bool dark in new bool[] { false, true })
                foreach (int width in new int[] { 900, Math.Max(900, Screen.PrimaryScreen.WorkingArea.Width - 40) }) LayoutTest(width, dark);
            MeasurementTest();
            Console.WriteLine("WINDOWS TESTS PASSED");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
