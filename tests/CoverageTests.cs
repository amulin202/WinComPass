using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class CoverageTests
{
    private static int assertions;
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            Exception error = e.ExceptionObject as Exception;
            Console.Error.WriteLine("UNHANDLED: " + (error == null ? "unknown" : error.GetType().FullName + ": " + error.Message + Environment.NewLine + error.StackTrace));
        };
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            MathCases(); GeodesicCases(); CoordinateCases(); DrawingCases(); SettingsCases();
            ProtocolCases().GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--ui") UiCases(args[1]);
            Console.WriteLine("PASS: " + assertions + " assertions (geometry, geodesy, coordinates, settings, WebSocket" +
                (args.Length > 0 ? ", WPF" : "") + ")");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
    private static void Equal(double expected, double actual, double tolerance, string message)
    { Check(CoverageMath.Finite(actual) && Math.Abs(expected - actual) <= tolerance, message + " expected=" + expected + " actual=" + actual); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }

    private static void MathCases()
    {
        CoverageParameters p = new CoverageParameters(); CoverageResult r = CoverageMath.Calculate(p);
        Equal(170.1384545885313, r.Near.Value, 1e-8, "near sample");
        Equal(285.43093362667753, r.Center.Value, 1e-8, "center sample");
        Equal(859.0875984874681, r.Far.Value, 1e-8, "far sample");
        Check(r.Near < r.Center && r.Center < r.Far, "ordered ranges");
        p.SiteElevation = 100; p.TargetElevation = 90; p.ReceiverHeight = 1.5;
        Equal(38.5, CoverageMath.Calculate(p).Height, 1e-8, "elevation and receiver height");
        p = new CoverageParameters { Mode = CoverageMode.TargetDistance, TargetDistance = 1000 };
        r = CoverageMath.Calculate(p); Equal(1000, r.Far.Value, 1e-7, "far target inversion");
        Equal(2, p.MechanicalTilt, 0, "mechanical tilt preserved"); Equal(4, p.ElectricalTilt, 0, "electrical tilt preserved");
        p.Binding = TargetBinding.Center; r = CoverageMath.Calculate(p);
        Equal(1000, r.Center.Value, 1e-7, "center target inversion");
        Check(!r.Far.HasValue, "center target may have no finite far edge");
        p = new CoverageParameters { MechanicalTilt = 0, ElectricalTilt = 4 };
        r = CoverageMath.Calculate(p); Check(!r.Far.HasValue && r.Truncated, "horizon must not produce a huge/negative far radius");
        p.ElectricalTilt = 0; r = CoverageMath.Calculate(p);
        Check(r.Near.HasValue && !r.Center.HasValue && !r.Far.HasValue, "horizontal main axis");
        p.ElectricalTilt = -5; Reject(delegate { CoverageMath.Calculate(p); }, "entire beam above horizon");
        p = new CoverageParameters { MechanicalTilt = 86, ElectricalTilt = 0 };
        Equal(0, CoverageMath.Calculate(p).Near.Value, 1e-8, "vertical near ray");
        p.MechanicalTilt = 87; Reject(delegate { CoverageMath.Calculate(p); }, "behind-station near ray");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { AntennaHeight = 0 }); }, "zero height");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { HorizontalBeamwidth = 0 }); }, "zero beam width");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { VerticalBeamwidth = double.NaN }); }, "NaN");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { Transparency = 101 }); }, "opacity range");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { Latitude = 91 }); }, "latitude range");
        Reject(delegate { CoverageMath.Calculate(new CoverageParameters { Mode = (CoverageMode)99 }); }, "invalid saved mode");
        p = new CoverageParameters { DisplayLimit = 50, GroundBandOnly = true };
        r = CoverageMath.Calculate(p); CoverageGeometry g = CoverageMath.BuildGeometry(p, r);
        Check(r.Truncated && !r.HasNearPoint && g.Fill.Count == 0, "empty visible ground band");
        Check(g.Lines.Exists(delegate(CoveragePath path) { return path.Name.StartsWith("显示截断"); }), "explicit clipped edge");
        Check(!g.Markers.Exists(delegate(CoverageMarker marker) { return marker.Name.StartsWith("远点"); }), "no false far label at clip");
        p = new CoverageParameters { Azimuth = 350 };
        r = CoverageMath.Calculate(p); g = CoverageMath.BuildGeometry(p, r);
        GeoLine left = Geodesy.Inverse(p.Station, g.Fill[1]);
        GeoLine right = Geodesy.Inverse(p.Station, g.Fill[g.Fill.Count - 1]);
        Equal(317.5, left.Azimuth, 1e-7, "sector across north left"); Equal(22.5, right.Azimuth, 1e-7, "sector across north right");
        p.HorizontalBeamwidth = 360; g = CoverageMath.BuildGeometry(p, CoverageMath.Calculate(p));
        Check(g.Fill.Count >= 362, "full circle supported");
    }

    private static void GeodesicCases()
    {
        GeoPoint zero = new GeoPoint(0, 0);
        GeoLine east = Geodesy.Inverse(zero, new GeoPoint(0, 1));
        Equal(111319.490793, east.Distance, 0.001, "WGS84 equator one degree"); Equal(90, east.Azimuth, 1e-8, "east bearing");
        Equal(110574.388558, Geodesy.Inverse(zero, new GeoPoint(1, 0)).Distance, 0.001, "WGS84 meridian one degree");
        Equal(1, Geodesy.Direct(zero, 90, east.Distance).Longitude, 1e-8, "direct equator");
        GeoPoint start = new GeoPoint(39.9, 116.4);
        foreach (double azimuth in new double[] { 0, 90, 180, 270, 359.5 })
        {
            GeoLine roundtrip = Geodesy.Inverse(start, Geodesy.Direct(start, azimuth, 9876.5));
            Equal(9876.5, roundtrip.Distance, 0.001, "direct/inverse distance");
            Equal(azimuth, roundtrip.Azimuth, 1e-7, "direct/inverse bearing");
        }
        GeoPoint dateLine = Geodesy.Direct(new GeoPoint(0, 179.99), 90, 5000);
        Check(dateLine.Longitude < -179, "antimeridian wrap");
        Equal(5000, Geodesy.Inverse(new GeoPoint(0, 179.99), dateLine).Distance, 0.001, "antimeridian distance");
        GeoPoint polar = new GeoPoint(89.9, 0);
        Equal(1000, Geodesy.Inverse(polar, Geodesy.Direct(polar, 30, 1000)).Distance, 0.001, "polar roundtrip");
        Equal(0, Geodesy.Inverse(start, start).Distance, 0, "coincident points");
        Reject(delegate { Geodesy.Inverse(zero, new GeoPoint(0, 180)); }, "antipodal nonconvergence is explicit");
    }

    private static void CoordinateCases()
    {
        GeoPoint wgs = new GeoPoint(39.911954, 116.377817), gcj = CoverageCoordinates.ToGcj02(wgs);
        // Upstream cross-language fixtures use 1e-6 degrees (different axes).
        Equal(39.91334545536069, gcj.Latitude, 1e-6, "reference Beijing GCJ latitude");
        Equal(116.38404722455657, gcj.Longitude, 1e-6, "reference Beijing GCJ longitude");
        GeoPoint roundtrip = CoverageCoordinates.FromGcj02(gcj);
        Equal(wgs.Latitude, roundtrip.Latitude, 1e-8, "GCJ inverse latitude"); Equal(wgs.Longitude, roundtrip.Longitude, 1e-8, "GCJ inverse longitude");
        GeoPoint abroad = new GeoPoint(40, -74);
        Equal(-74, CoverageCoordinates.ToGcj02(abroad).Longitude, 0, "outside China unchanged");
        Equal(wgs.Latitude, CoverageCoordinates.FromOvit(wgs.Latitude, wgs.Longitude, 0).Latitude, 0, "Ovit real flag");
        Reject(delegate { CoverageCoordinates.FromOvit(1, 2, 2); }, "do not treat local projected coordinates as degrees");
    }

    private static void DrawingCases()
    {
        CoverageParameters p = new CoverageParameters();
        List<OvitObject> objects = OvitDrawing.Objects(p, CoverageMath.BuildGeometry(p, CoverageMath.Calculate(p)));
        int fills = 0; HashSet<string> keys = new HashSet<string>();
        foreach (OvitObject item in objects)
        {
            Check(keys.Add(item.Key), "unique object key");
            Dictionary<string, object> detail = OvitJson.Object(OvitJson.Object(item.Data["Object"])["ObjectDetail"]);
            Equal(0, (int)detail["Gcj02"], 0, "real coordinates sent");
            if (item.Type == 13)
            {
                fills++; Equal(OvitDrawing.Blue, (int)detail["AreaClr"], 0, "COLORREF blue");
                Equal(70, (int)detail["AreaAlpha"], 0, "independent fill transparency");
                double[] coordinates = (double[])detail["Latlng"];
                Equal(p.Latitude, coordinates[0], 1e-10, "flatten latitude first"); Equal(p.Longitude, coordinates[1], 1e-10, "flatten longitude second");
            }
            if (item.Type == 8)
                Equal(OvitDrawing.Red, (int)OvitJson.Object(detail["TrackDraw"])["LineClr"], 0, "red lines");
        }
        Equal(1, fills, 0, "single fill avoids opacity stacking");
        Dictionary<string, object> tree = OvitJson.Object(OvitDrawing.Tree(objects[0], "12345")["ObjTree"]);
        Equal(202, (int)tree["ParentID"], 0, "temporary parent"); Equal(1, (int)tree["idxTree"], 0, "temporary tree index");
        Check((string)objects[0].Data["ObjID"] == "12345", "reuse acknowledged id");
        Check(OvitClient.PasswordDigest("123456") == "E10ADC3949BA59AB", "official authentication digest");
        Check(OvitClient.PasswordDigest("") == "", "empty key");
        Check(OvitJson.Deserialize(OvitJson.Serialize(tree)).ContainsKey("Object"), "JSON roundtrip");
    }

    private static void SettingsCases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CompassSettingsTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "parameters.xml");
        try
        {
            CoverageSettings settings = new CoverageSettings { Parameters = new CoverageParameters { Mode = CoverageMode.TargetDistance,
                Transparency = 83, GroundBandOnly = true }, CoordinateType = 1, Address = "ws://localhost:7699" };
            CoverageSettingsStore.Save(path, settings); CoverageSettings loaded = CoverageSettingsStore.Load(path);
            Equal(83, loaded.Parameters.Transparency, 0, "save/load transparency");
            Check(loaded.Parameters.Mode == CoverageMode.TargetDistance && loaded.Parameters.GroundBandOnly && loaded.CoordinateType == 1, "save/load modes");
            Check(!File.ReadAllText(path).Contains("password") && !File.ReadAllText(path).Contains("md5psk"), "no secret persisted");
            settings.Parameters.Transparency = 25; CoverageSettingsStore.Save(path, settings);
            Equal(25, CoverageSettingsStore.Load(path).Parameters.Transparency, 0, "atomic replace existing settings");
            File.WriteAllText(path, "<!DOCTYPE a [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><CoverageSettings>&e;</CoverageSettings>");
            bool rejected = false; try { CoverageSettingsStore.Load(path); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "DTD disabled in imported settings");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    private static async Task ProtocolCases()
    {
        using (MockOvit server = new MockOvit())
        using (OvitClient client = new OvitClient())
        {
            await client.ConnectAsync(server.Address, "123456"); Check(server.SawSubprotocol, "WebSocket ws subprotocol");
            Check(server.RegisteredKey == "E10ADC3949BA59AB", "authentication sent");
            Dictionary<string, object> membership = await client.RequestAsync(20501, OvitJson.Map(), 1000);
            Equal(9, OvitJson.Number(membership, "OmapLevel"), 0, "membership reply");
            GeoPoint picked = await client.PickAsync();
            Equal(39.911954, picked.Latitude, 1e-6, "picked GCJ coordinate normalized");
            await client.LocateAsync(picked); Check(server.LocationPreserved, "map jump keeps basemap and zoom");
            OvitDrawingSession session = new OvitDrawingSession(client);
            CoverageParameters p = new CoverageParameters(); CoverageGeometry geometry = CoverageMath.BuildGeometry(p, CoverageMath.Calculate(p));
            await session.UpdateAsync(p, geometry); int created = server.Objects.Count;
            Check(created > 5, "all map components acknowledged");
            p.Transparency = 90; await session.UpdateAsync(p, geometry);
            Equal(created, server.Objects.Count, 0, "style update reuses ids");
            p.ElectricalTilt = 2; geometry = CoverageMath.BuildGeometry(p, CoverageMath.Calculate(p));
            await session.UpdateAsync(p, geometry); Check(server.Objects.Count > 1, "horizon update removes stale finite far components");
            await session.ClearAsync(); Equal(1, server.Objects.Count, 0, "only sentinel survives targeted clear");
            Check(server.Objects.ContainsKey(9999), "unrelated map object preserved");
            Check(!server.GlobalClear, "never globally clear temporary objects");
            bool memberError = false;
            try { await client.RequestAsync(99000, OvitJson.Map(), 1000); }
            catch (IOException error) { memberError = error.Message.Contains("会员"); }
            Check(memberError && client.IsConnected, "permission error is reported without corrupting session");
            Task<Dictionary<string, object>> a = client.RequestAsync(99001, OvitJson.Map("value", 1), 1000);
            Task<Dictionary<string, object>> b = client.RequestAsync(99001, OvitJson.Map("value", 2), 1000);
            Equal(1, OvitJson.Number(await a, "value"), 0, "serialized request first response");
            Equal(2, OvitJson.Number(await b, "value"), 0, "serialized request second response");
            bool timeout = false;
            try { await client.RequestAsync(99002, OvitJson.Map(), 100); }
            catch (IOException) { timeout = true; }
            Check(timeout && !client.IsConnected, "timeout retires connection to isolate late replies");
        }
        using (MockOvit server = new MockOvit { RejectAuthentication = true })
        using (OvitClient client = new OvitClient())
        {
            bool rejected = false; try { await client.ConnectAsync(server.Address, "bad"); } catch (IOException) { rejected = true; }
            Check(rejected, "incorrect shared key rejected");
        }
        using (MockOvit server = new MockOvit { HoldPick = true })
        using (OvitClient client = new OvitClient())
        {
            await client.ConnectAsync(server.Address, "");
            Task<GeoPoint> pending = client.PickAsync(); client.Dispose();
            bool canceled = false; try { await pending; } catch (Exception) { canceled = true; }
            Check(canceled, "cancel releases pending map pick");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (DependencyObject item in Descendants(child)) yield return item;
        }
    }
    private static T Control<T>(Window window, string id) where T : DependencyObject
    {
        foreach (DependencyObject child in Descendants(window))
            if (child is T && AutomationProperties.GetAutomationId(child) == id) return (T)child;
        throw new Exception("UI control not found: " + id);
    }
    private static bool HasText(Window window, string text)
    {
        foreach (DependencyObject child in Descendants(window))
        { TextBlock label = child as TextBlock; if (label != null && label.Text.Contains(text)) return true; }
        return false;
    }
    private static Button FindButton(Window window, string content)
    {
        foreach (DependencyObject child in Descendants(window))
        { Button button = child as Button; if (button != null && Convert.ToString(button.Content) == content) return button; }
        throw new Exception("Button not found: " + content);
    }
    private static void Click(Window window, string content)
    {
        Button button = FindButton(window, content); Check(button.IsEnabled, "button enabled: " + content);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Until(Func<bool> condition, string message)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 5000) Pump();
        Check(condition(), message);
    }
    private static void Pump()
    {
        DispatcherFrame frame = new DispatcherFrame();
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void UiCases(string directory)
    {
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Exception uiException = null;
        app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Console.Error.WriteLine("UI UNHANDLED: " + e.Exception.GetType().FullName + ": " + e.Exception.Message + Environment.NewLine + e.Exception.StackTrace);
            uiException = e.Exception;
            e.Handled = true;
        };
        CoverageWindow window = new CoverageWindow(null, false) { ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Pump(); window.UpdateLayout();
            Check(HasText(window, "170.14 m") && HasText(window, "285.43 m") && HasText(window, "859.09 m"), "initial WPF sample results");
            GeoPoint manualTarget = Geodesy.Direct(new GeoPoint(39.9042, 116.4074), 15, 1234);
            Control<TextBox>(window, "TargetLatitude").Text = manualTarget.Latitude.ToString("R", CultureInfo.InvariantCulture);
            Control<TextBox>(window, "TargetLongitude").Text = manualTarget.Longitude.ToString("R", CultureInfo.InvariantCulture);
            foreach (DependencyObject child in Descendants(window))
            {
                Button button = child as Button;
                if (button != null && Convert.ToString(button.Content) == "应用终点坐标") { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break; }
            }
            Equal(1234, double.Parse(Control<TextBox>(window, "TargetDistance").Text, CultureInfo.InvariantCulture), 0.01, "manual endpoint is retained before recalculation");
            Equal(15, double.Parse(Control<TextBox>(window, "Azimuth").Text, CultureInfo.InvariantCulture), 1e-6, "manual endpoint bearing");
            Control<ComboBox>(window, "CoverageMode").SelectedIndex = 1;
            Control<ComboBox>(window, "TargetBinding").SelectedIndex = 1;
            Control<TextBox>(window, "TargetDistance").Text = "500"; Pump();
            Check(HasText(window, "500.00 m"), "UI center target binding");
            Control<ComboBox>(window, "CoverageMode").SelectedIndex = 0;
            Control<TextBox>(window, "Azimuth").Text = "120";
            Control<TextBox>(window, "TargetDistance").Text = "859.0875984874681";
            Control<TextBox>(window, "Latitude").Text = "NaN"; Pump();
            Check(HasText(window, "请输入有效数字") && HasText(window, "—"), "invalid edit clears preview results");
            Control<TextBox>(window, "Latitude").Text = "39.9042"; Pump();
            Check(HasText(window, "170.14 m") && HasText(window, "参数已更新"), "valid edit recovers UI");
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int red = 0, blue = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 2] > 180 && pixels[i] < 130 && pixels[i + 1] < 130) red++;
                if (pixels[i] > pixels[i + 2] + 25 && pixels[i] > pixels[i + 1] + 25) blue++;
            }
            Check(red > 100 && blue > 100, "render contains red boundary and blue fill");
            string screenshot = Path.Combine(directory, "coverage-preview.png");
            using (FileStream stream = File.Create(screenshot))
            { PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
            Console.WriteLine("UI preview: " + Path.GetFullPath(screenshot));
            Control<Slider>(window, "FillTransparency").Value = 100; Pump();
            Check(HasText(window, "100%"), "transparency slider reaches 100 percent");
            Control<Slider>(window, "FillTransparency").Value = 70; Pump();
            using (MockOvit server = new MockOvit())
            {
                Control<TextBox>(window, "OvitAddress").Text = server.Address;
                Click(window, "连接奥维"); Until(delegate { return HasText(window, "VIP 9"); }, "UI connection and membership");
                GeoPoint station = new GeoPoint(39.9, 116.4), target = Geodesy.Direct(station, 75, 700);
                server.PickPoints.Enqueue(station); server.PickPoints.Enqueue(target);
                Control<ComboBox>(window, "CoverageMode").SelectedIndex = 1;
                Control<ComboBox>(window, "TargetBinding").SelectedIndex = 0; Pump();
                Click(window, "地图拉线：起点 → 终点");
                Until(delegate { return HasText(window, "操作完成"); }, "UI two-point map line complete");
                Equal(700, double.Parse(Control<TextBox>(window, "TargetDistance").Text, CultureInfo.InvariantCulture), 0.01, "map line distance reaches UI");
                Equal(75, double.Parse(Control<TextBox>(window, "Azimuth").Text, CultureInfo.InvariantCulture), 1e-6, "map line bearing reaches UI");
                Check(HasText(window, "700.00 m"), "map line constrains far result");
                Click(window, "生成 / 更新到奥维"); Until(delegate { return FindButton(window, "断开连接").IsEnabled; }, "UI publish complete");
                Check(server.Objects.Count > 5, "UI publishes all map components");
                foreach (DependencyObject child in Descendants(window))
                {
                    CheckBox check = child as CheckBox;
                    if (check != null && Convert.ToString(check.Content) == "参数变化时更新奥维") { check.IsChecked = true; break; }
                }
                int writes = server.WriteCount;
                Control<Slider>(window, "FillTransparency").Value = 45;
                Until(delegate { return server.WriteCount > writes && FindButton(window, "断开连接").IsEnabled; }, "UI automatic style update");
                int stable = server.WriteCount; Pump(); Pump(); Pump(); Pump();
                Equal(stable, server.WriteCount, 0, "automatic update settles without repeat loop");
                Click(window, "清除本次扇形"); Until(delegate { return FindButton(window, "断开连接").IsEnabled; }, "UI targeted clear complete");
                Check(server.Objects.Count == 1 && server.Objects.ContainsKey(9999), "UI clear preserves unrelated object");
                Click(window, "断开连接");
            }
            Check(uiException == null, "no unhandled UI events");
        }
        finally { window.Close(); app.Shutdown(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}

// An in-process RFC6455 peer. It uses an ephemeral loopback TCP port, so tests
// need neither a member account nor HTTP.sys URL reservations/external packages.
internal sealed class MockOvit : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private TcpClient peer;
    private bool disposed;
    private long nextId = 10000;
    internal readonly Dictionary<long, object> Objects = new Dictionary<long, object> { { 9999, "unrelated" } };
    internal readonly Queue<GeoPoint> PickPoints = new Queue<GeoPoint>();
    internal volatile int WriteCount;
    internal bool SawSubprotocol, LocationPreserved, GlobalClear, RejectAuthentication, HoldPick;
    internal string RegisteredKey, Address;
    internal MockOvit()
    {
        listener.Start(); Address = "ws://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run((Func<Task>)ServeAsync); GC.KeepAlive(server);
    }
    private async Task ServeAsync()
    {
        try
        {
            peer = await listener.AcceptTcpClientAsync(); NetworkStream stream = peer.GetStream();
            StringBuilder headers = new StringBuilder();
            while (!headers.ToString().EndsWith("\r\n\r\n"))
            { int value = stream.ReadByte(); if (value < 0) return; headers.Append((char)value); if (headers.Length > 16384) return; }
            string key = null;
            foreach (string line in headers.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) key = line.Substring(line.IndexOf(':') + 1).Trim();
                if (line.StartsWith("Sec-WebSocket-Protocol:", StringComparison.OrdinalIgnoreCase) && line.Contains("ws")) SawSubprotocol = true;
            }
            string accept;
            using (SHA1 sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            byte[] handshake = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\nSec-WebSocket-Protocol: ws\r\n\r\n");
            await stream.WriteAsync(handshake, 0, handshake.Length);
            while (!disposed)
            {
                string input = ReadFrame(stream); if (input == null) break;
                Dictionary<string, object> request = OvitJson.Deserialize(input);
                int command = (int)OvitJson.Number(request, "cmdid");
                Dictionary<string, object> msg = OvitJson.Object(request["msg"]), response = OvitJson.Map(); int result = 0;
                switch (command)
                {
                    case 20000: RegisteredKey = Convert.ToString(msg["md5psk"]); if (RejectAuthentication) response["errmsg"] = "Pre-shared key error"; break;
                    case 20501: response = OvitJson.Map("OmapLevel", 9, "LoadOK", 1); break;
                    case 20001:
                        if (HoldPick) continue;
                        if (PickPoints.Count > 0)
                        {
                            GeoPoint picked = PickPoints.Dequeue(); response = OvitJson.Map("lat", picked.Latitude, "lng", picked.Longitude, "CoordType", 0);
                        }
                        else response = OvitJson.Map("lat", 39.91334545536069, "lng", 116.38404722455657, "CoordType", 1);
                        break;
                    case 20012: response = OvitJson.Map("MapType", 6, "MapLevel", 15); break;
                    case 20011:
                        Dictionary<string, object> location = OvitJson.Object(msg["Location"]);
                        LocationPreserved = (int)location["MapID"] == 6 && (int)location["MapLevel"] == 15 && (int)location["CoordType"] == 0; break;
                    case 20112:
                        WriteCount++;
                        Dictionary<string, object> tree = OvitJson.Object(msg["ObjTree"]);
                        Dictionary<string, object> detail = OvitJson.Object(OvitJson.Object(tree["Object"])["ObjectDetail"]);
                        ArrayList children = (ArrayList)detail["ObjChildren"];
                        Dictionary<string, object> child = OvitJson.Object(children[0]); long id = Convert.ToInt64(child["ObjID"]);
                        if (id == 0) id = nextId++; Objects[id] = child;
                        response["ObjItems"] = new object[] { OvitJson.Map("Type", child["Type"], "ObjID", id.ToString(), "ParentID", 202) }; break;
                    case 20103: foreach (object idValue in (ArrayList)msg["ListID"]) Objects.Remove(Convert.ToInt64(idValue)); break;
                    case 20010: GlobalClear = true; break;
                    case 99000: result = -5; break;
                    case 99001: response = msg; break;
                    case 99002: continue;
                    default: result = -4; break;
                }
                byte[] payload = Encoding.UTF8.GetBytes(OvitJson.Serialize(OvitJson.Map("cmdid", command, "ret", result, "msg", response)));
                // Fragment all replies to exercise receive assembly across frames.
                int split = payload.Length / 2; SendFrame(stream, payload, 0, split, 1); SendFrame(stream, payload, split, payload.Length - split, 128);
            }
        }
        catch (Exception) { if (!disposed) { /* A deliberately aborted timeout/cancel connection is expected. */ } }
    }
    private static string ReadFrame(Stream stream)
    {
        int first = stream.ReadByte(), second = stream.ReadByte(); if (first < 0 || second < 0 || (first & 15) == 8) return null;
        int length = second & 127;
        if (length == 126) length = (stream.ReadByte() << 8) | stream.ReadByte();
        else if (length == 127)
        { long wide = 0; for (int i = 0; i < 8; i++) wide = (wide << 8) | (byte)stream.ReadByte(); if (wide > 8 * 1024 * 1024) throw new IOException(); length = (int)wide; }
        byte[] mask = new byte[4]; if ((second & 128) != 0) ReadFully(stream, mask);
        byte[] payload = new byte[length]; ReadFully(stream, payload);
        if ((second & 128) != 0) for (int i = 0; i < length; i++) payload[i] ^= mask[i % 4];
        return Encoding.UTF8.GetString(payload);
    }
    private static void ReadFully(Stream stream, byte[] data)
    { int offset = 0; while (offset < data.Length) { int read = stream.Read(data, offset, data.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } }
    private static void SendFrame(Stream stream, byte[] data, int offset, int count, int opcode)
    {
        stream.WriteByte((byte)opcode);
        if (count < 126) stream.WriteByte((byte)count);
        else { stream.WriteByte(126); stream.WriteByte((byte)(count >> 8)); stream.WriteByte((byte)count); }
        stream.Write(data, offset, count); stream.Flush();
    }
    public void Dispose()
    { disposed = true; if (peer != null) peer.Close(); listener.Stop(); }
}
