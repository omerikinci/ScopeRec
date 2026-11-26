using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms.DataVisualization.Charting;

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
            Splitter splitter = (Splitter)Field(f, "serSplit");
            Check(splitter.Width >= 14 && splitter.Cursor == Cursors.VSplit, "Serial grip is too small");
            int originalWidth = serial.Width;
            splitter.SplitPosition = originalWidth + 20; Pump();
            Check(serial.Width > originalWidth, "Serial pane cannot grow");
            splitter.SplitPosition = originalWidth; Pump();
            Check(serial.Width == originalWidth, "Serial pane cannot shrink: original=" + originalWidth + " actual=" + serial.Width + " position=" + splitter.SplitPosition + " min=" + splitter.MinSize);
            ComboBox window = (ComboBox)Field(f, "cmbWindow");
            Check(window.BackColor != Ui.Th.Input, "Time span has no distinct background");
            Check(window.Parent.ClientRectangle.Contains(window.Bounds), "Time span is clipped");
            Control[] clear = f.Controls.Find("clearChartButton", true);
            Check(clear.Length == 1 && clear[0].Parent.ClientRectangle.Contains(clear[0].Bounds), "Clear chart is clipped");
            Rectangle clearScreen = clear[0].RectangleToScreen(clear[0].ClientRectangle);
            for (Control ancestor = clear[0].Parent; ancestor != null; ancestor = ancestor.Parent)
                Check(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(clearScreen), "Clear chart extends outside " + ancestor.GetType().Name);
            SerialClearTest(f);
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

    static void SerialClearTest(MainForm f)
    {
        Array sessions = (Array)Field(f, "ses");
        object first = sessions.GetValue(0), second = sessions.GetValue(1);
        Type type = first.GetType();
        TextBox log = (TextBox)type.GetField("Log").GetValue(first);
        TextBox otherLog = (TextBox)type.GetField("Log").GetValue(second);
        StringBuilder text = (StringBuilder)type.GetField("Text").GetValue(first);
        Queue<string> queue = (Queue<string>)type.GetField("Queue").GetValue(first);
        text.Append("old data"); queue.Enqueue("pending data"); log.Text = "old data";
        otherLog.Text = "keep this";
        string path = Path.GetTempFileName();
        try
        {
            using (StreamWriter writer = new StreamWriter(path))
            {
                type.GetField("Writer").SetValue(first, writer);
                typeof(MainForm).GetMethod("ClearSerialTerminal", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f, new object[] { first });
                Check(log.Text.Length == 0 && text.Length == 0 && queue.Count == 0, "Serial terminal was not cleared");
                Check(otherLog.Text == "keep this", "Clearing affected another terminal");
                writer.WriteLine("recording continues"); writer.Flush();
                Check(new FileInfo(path).Length > 0, "Clearing closed serial recording");
                type.GetField("Writer").SetValue(first, null);
            }
        }
        finally { File.Delete(path); }
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
                Call(f, "RefreshUi");
                Chart chart = (Chart)Field(f, "chart");
                Check(chart.Series.Count > 0, "Chart has no measurement series");
                Check(chart.Series[0].LegendText == "C1_RMS [V]", "Legend measurement name mismatch");
                Check(chart.Series[0].Color == Ui.Th.Chan[0], "Legend and channel color mismatch");
                using (Bitmap render = new Bitmap(chart.Width, chart.Height))
                    chart.DrawToBitmap(render, new Rectangle(Point.Empty, render.Size));
                ChartArea area = chart.ChartAreas[0];
                List<SeriesData> hoverData = (List<SeriesData>)Field(f, "series");
                double hoverTime = hoverData[0].T[0];
                Point mouse = new Point((int)area.AxisX.ValueToPixelPosition(hoverTime),
                    (int)area.AxisY.ValueToPixelPosition((area.AxisY.Minimum + area.AxisY.Maximum) / 2));
                typeof(MainForm).GetField("hoverPt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f, mouse);
                typeof(MainForm).GetField("hovering", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f, true);
                Call(f, "UpdateHover");
                Label tip = (Label)Field(f, "lblHover");
                Check(tip.Visible && tip.Text.Contains("C1_RMS"), "Hover is missing the measurement name");
                Check(chart.ClientRectangle.Contains(tip.Bounds), "Hover extends outside chart");
                long before = (long)Field(f, "samples");
                List<SeriesData> data = (List<SeriesData>)Field(f, "series");
                long count = data[0].N;
                Call(f, "ClearChart");
                Check((bool)Field(f, "running"), "Clear chart stopped measurement");
                Check((long)Field(f, "samples") >= before && data[0].N >= count, "Clear chart reset statistics");
                Check(chart.Series[0].Points.Count == 0, "Clear chart retained old points");
                deadline = DateTime.UtcNow.AddSeconds(5);
                while ((long)Field(f, "samples") <= before && DateTime.UtcNow < deadline) Pump();
                Check((long)Field(f, "samples") > before, "Measurement did not continue after clearing");
                Call(f, "ShiftChart", -0.25);
                Call(f, "ShowAllTime"); Call(f, "AutoY"); Call(f, "FollowLive");
                Call(f, "EndTest", false);
                Check(!(bool)Field(f, "running"), "Measurement did not stop");
                Call(f, "ClearChart");
                Check(data[0].T.Count == 0 && data[0].N > 0, "Idle clear lost statistics or retained points");
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

    static void CancelReadTest()
    {
        TcpListener server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using (ILink link = TcpLink.Open("127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port, 15000))
        using (TcpClient peer = server.AcceptTcpClient())
        {
            bool interrupted = false;
            Thread reader = new Thread(delegate()
            {
                try { link.QueryText("*IDN?"); }
                catch (IOException) { interrupted = true; }
                catch (ObjectDisposedException) { interrupted = true; }
            });
            reader.IsBackground = true; reader.Start();
            peer.ReceiveTimeout = 2000;
            peer.GetStream().ReadByte();
            link.Cancel();
            Check(reader.Join(2000), "Stop did not interrupt a pending Windows read");
            Check(interrupted, "Pending read was not cancelled");
        }
        server.Stop();
        Console.WriteLine("PASS cancellation of a pending instrument read");
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
            CancelReadTest();
            Console.WriteLine("WINDOWS TESTS PASSED");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
