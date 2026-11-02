using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

public sealed class CoverageSettings
{
    public CoverageParameters Parameters = new CoverageParameters();
    public string Address = "ws://127.0.0.1:7699";
    public int CoordinateType;
}

internal static class CoverageSettingsStore
{
    internal static string DefaultPath
    { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compass", "coverage.xml"); } }

    internal static CoverageSettings Load(string path)
    {
        XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
        using (XmlReader reader = XmlReader.Create(path, options))
        {
            CoverageSettings settings = (CoverageSettings)new XmlSerializer(typeof(CoverageSettings)).Deserialize(reader);
            if (settings == null || settings.Parameters == null) throw new IOException("配置文件缺少天线参数。");
            CoverageMath.Calculate(settings.Parameters);
            if (settings.CoordinateType < 0 || settings.CoordinateType > 1) throw new IOException("配置文件中的坐标类型无效。");
            return settings;
        }
    }

    internal static void Save(string path, CoverageSettings settings)
    {
        CoverageMath.Calculate(settings.Parameters);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".compass-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(CoverageSettings)).Serialize(stream, settings);
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class CoverageWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(31, 46, 65));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(99, 116, 137));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(24, 87, 159));
    private readonly Dictionary<string, TextBox> inputs = new Dictionary<string, TextBox>();
    private readonly List<Button> mapButtons = new List<Button>();
    private readonly ComboBox mode = new ComboBox(), binding = new ComboBox(), coordinateType = new ComboBox();
    private readonly TextBox address = new TextBox(), targetLatitude = new TextBox(), targetLongitude = new TextBox();
    private readonly PasswordBox password = new PasswordBox();
    private readonly CheckBox groundBand = new CheckBox { Content = "仅填充近点至远点" };
    private readonly CheckBox autoMap = new CheckBox { Content = "参数变化时更新奥维", IsEnabled = false };
    private readonly Slider transparency = new Slider { Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock transparencyText = new TextBlock(), status = new TextBlock(), notice = new TextBlock();
    private readonly TextBlock nearValue = new TextBlock(), centerValue = new TextBlock(), farValue = new TextBlock();
    private readonly TextBlock tiltValue = new TextBlock(), targetValue = new TextBlock(), memberValue = new TextBlock();
    private readonly CoveragePreview preview = new CoveragePreview();
    private readonly Button connect = new Button(), cancel = new Button();
    private readonly DispatcherTimer inputTimer, mapTimer;
    private readonly CompassForm compass;
    private readonly bool persist;
    private OvitClient client;
    private OvitDrawingSession drawing;
    private CoverageParameters current;
    private CoverageResult calculated;
    private CoverageGeometry geometry;
    private bool loading = true, busy, picking, closing, calculationError;
    private int revision, publishedRevision = -1;

    internal CoverageWindow(CompassForm compass) : this(compass, true) { }
    internal CoverageWindow(CompassForm compass, bool persist)
    {
        this.compass = compass; this.persist = persist;
        Title = "天线覆盖 · Compass";
        Width = 1080; Height = Math.Min(850, SystemParameters.WorkArea.Height - 35);
        MinWidth = 960; MinHeight = Math.Min(650, SystemParameters.WorkArea.Height - 60);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 12; Foreground = Ink;
        inputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        inputTimer.Tick += delegate { inputTimer.Stop(); Recalculate(); };
        mapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        mapTimer.Tick += async delegate
        {
            mapTimer.Stop();
            if (autoMap.IsChecked != true || current == null || !Connected) return;
            if (busy) { mapTimer.Start(); return; }
            await RunMapAsync(PublishAsync, "正在更新奥维扇形…");
        };
        Content = BuildContent();
        preview.TargetChanged += delegate(double azimuth, double distance)
        {
            loading = true; Set("Azimuth", azimuth); Set("TargetDistance", distance); loading = false;
            inputTimer.Stop(); Recalculate();
        };
        mode.SelectionChanged += delegate { QueueChange(); };
        binding.SelectionChanged += delegate { QueueChange(); };
        groundBand.Checked += delegate { QueueChange(); }; groundBand.Unchecked += delegate { QueueChange(); };
        transparency.ValueChanged += delegate { QueueChange(); };
        coordinateType.SelectionChanged += delegate
        {
            if (loading || current == null) return;
            loading = true; WriteStation(current.Station); WriteTarget(calculated.TargetPoint); loading = false;
            Recalculate();
        };
        autoMap.Checked += delegate { QueueMap(); };
        autoMap.Unchecked += delegate { mapTimer.Stop(); };
        Closing += delegate
        {
            closing = true; inputTimer.Stop(); mapTimer.Stop();
            if (persist && current != null)
            {
                try { CoverageSettingsStore.Save(CoverageSettingsStore.DefaultPath, Settings()); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (client != null) client.Dispose();
            if (compass != null) compass.AzimuthChanged -= CompassAzimuthChanged;
        };
        CoverageSettings initial = new CoverageSettings();
        string loadError = null;
        if (persist && File.Exists(CoverageSettingsStore.DefaultPath))
        {
            try { initial = CoverageSettingsStore.Load(CoverageSettingsStore.DefaultPath); }
            catch (Exception error) { loadError = "已使用默认参数；上次配置读取失败：" + error.Message; }
        }
        if (compass != null) initial.Parameters.Azimuth = compass.Azimuth;
        LoadSettings(initial);
        if (compass != null) compass.AzimuthChanged += CompassAzimuthChanged;
        if (loadError != null) SetStatus(loadError, true);
        else SetStatus("可先在右侧预览；连接会员版奥维后，可在地图选站点及拉线终点。", false);
    }

    private UIElement BuildContent()
    {
        Grid root = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "天线覆盖", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "按工参查看波瓣落点，或按目标位置反算下倾角", Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        root.Children.Add(heading);

        DockPanel connection = new DockPanel { LastChildFill = true };
        StackPanel connectionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        connect.Content = "连接奥维"; StyleButton(connect); connect.Click += async delegate { await ConnectAsync(); };
        cancel.Content = "取消取点"; StyleButton(cancel); cancel.IsEnabled = false;
        cancel.Click += delegate
        {
            Disconnect(); SetStatus("取点已取消。连接已断开，可重新连接后继续。", false);
        };
        connectionButtons.Children.Add(connect); connectionButtons.Children.Add(cancel);
        DockPanel.SetDock(connectionButtons, Dock.Right); connection.Children.Add(connectionButtons);
        StackPanel server = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        server.Children.Add(new TextBlock { Text = "奥维地址", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        address.Width = 216; address.Height = 29; address.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(address, "OvitAddress");
        server.Children.Add(address);
        server.Children.Add(new TextBlock { Text = "预共享密钥", Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        password.Width = 100; password.Height = 29; password.VerticalContentAlignment = VerticalAlignment.Center;
        server.Children.Add(password); memberValue.Foreground = Muted; memberValue.Margin = new Thickness(12, 0, 0, 0);
        memberValue.VerticalAlignment = VerticalAlignment.Center; server.Children.Add(memberValue); connection.Children.Add(server);
        Border connectionCard = Card(null, connection); Grid.SetRow(connectionCard, 1); root.Children.Add(connectionCard);

        Grid columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(376) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(columns, 2); root.Children.Add(columns);
        StackPanel form = new StackPanel();
        form.Children.Add(BuildLocation()); form.Children.Add(BuildParameters()); form.Children.Add(BuildStyle());
        WrapPanel files = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        files.Children.Add(Button("保存参数…", SaveParameters)); files.Children.Add(Button("读取参数…", LoadParameters));
        files.Children.Add(Button("恢复示例", delegate { LoadSettings(new CoverageSettings { Address = address.Text }); }));
        files.Children.Add(Button("第三方许可", delegate { ShowNotices(this); }));
        form.Children.Add(files);
        ScrollViewer scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        columns.Children.Add(scroll);

        Grid right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition()); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 2); columns.Children.Add(right);
        UniformGrid metrics = new UniformGrid { Columns = 3 };
        metrics.Children.Add(Metric("近点", nearValue)); metrics.Children.Add(Metric("中点", centerValue)); metrics.Children.Add(Metric("远点", farValue));
        right.Children.Add(metrics);
        Grid drawingArea = new Grid(); drawingArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        drawingArea.RowDefinitions.Add(new RowDefinition()); drawingArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        drawingArea.Children.Add(new TextBlock { Text = "覆盖预览", FontWeight = FontWeights.SemiBold, Foreground = Accent });
        Grid.SetRow(preview, 1); drawingArea.Children.Add(preview);
        TextBlock hint = new TextBlock { Text = "点击或拖动调整方向与目标距离 · 上北下南", Foreground = Muted,
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(hint, 2); drawingArea.Children.Add(hint);
        Border drawingCard = Card(null, drawingArea); Grid.SetRow(drawingCard, 1); right.Children.Add(drawingCard);
        StackPanel summary = new StackPanel(); tiltValue.TextWrapping = TextWrapping.Wrap; summary.Children.Add(tiltValue);
        targetValue.TextWrapping = TextWrapping.Wrap; targetValue.Foreground = Muted; targetValue.Margin = new Thickness(0, 6, 0, 0); summary.Children.Add(targetValue);
        notice.TextWrapping = TextWrapping.Wrap; notice.Foreground = Muted; notice.FontSize = 11; notice.Margin = new Thickness(0, 6, 0, 0); summary.Children.Add(notice);
        WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(MapButton("生成 / 更新到奥维", async delegate { await RunMapAsync(PublishAsync, "正在生成奥维扇形…"); }));
        actions.Children.Add(MapButton("清除本次扇形", async delegate
        { await RunMapAsync(async delegate { await drawing.ClearAsync(); publishedRevision = -1; autoMap.IsChecked = false; }, "正在清除本次扇形…"); }));
        summary.Children.Add(actions); autoMap.Margin = new Thickness(0, 8, 0, 0); summary.Children.Add(autoMap);
        Border summaryCard = Card(null, summary); Grid.SetRow(summaryCard, 2); right.Children.Add(summaryCard);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(2, 7, 0, 0); status.MinHeight = 22;
        Grid.SetRow(status, 3); root.Children.Add(status);
        return root;
    }

    private UIElement BuildLocation()
    {
        StackPanel panel = new StackPanel();
        coordinateType.Items.Add("WGS84 / CGCS2000（工程近似）"); coordinateType.Items.Add("GCJ-02");
        coordinateType.Height = 29; coordinateType.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(coordinateType);
        UniformGrid position = new UniformGrid { Columns = 2 };
        position.Children.Add(Field("站点经度", "Longitude", 116.4074)); position.Children.Add(Field("站点纬度", "Latitude", 39.9042)); panel.Children.Add(position);
        WrapPanel buttons = new WrapPanel();
        buttons.Children.Add(MapButton("地图选站点", async delegate
        {
            await RunMapAsync(async delegate
            {
                SetStatus("请在奥维地图中单击站点位置。", false);
                GeoPoint point = await client.PickAsync();
                loading = true; WriteStation(point); loading = false; Recalculate();
            }, "请在奥维地图中单击站点位置。", true);
        }));
        buttons.Children.Add(MapButton("定位到奥维", async delegate
        {
            await RunMapAsync(async delegate { RequireParameters(); await client.LocateAsync(current.Station); }, "正在定位站点…");
        }));
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = "拉线终点", Margin = new Thickness(0, 8, 0, 5), Foreground = Muted });
        UniformGrid terminal = new UniformGrid { Columns = 2 };
        AutomationProperties.SetAutomationId(targetLongitude, "TargetLongitude");
        AutomationProperties.SetAutomationId(targetLatitude, "TargetLatitude");
        terminal.Children.Add(PlainField("终点经度", targetLongitude)); terminal.Children.Add(PlainField("终点纬度", targetLatitude)); panel.Children.Add(terminal);
        WrapPanel targets = new WrapPanel();
        targets.Children.Add(Button("应用终点坐标", ApplyTargetCoordinates));
        targets.Children.Add(MapButton("地图选终点", async delegate { await PickLineAsync(false); }));
        targets.Children.Add(MapButton("地图拉线：起点 → 终点", async delegate { await PickLineAsync(true); }));
        panel.Children.Add(targets);
        panel.Children.Add(new TextBlock { Text = "工参模式：终点为目标参考；反算模式：终点约束中点或远点。",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
        return Card("站点与拉线", panel);
    }

    private UIElement BuildParameters()
    {
        StackPanel panel = new StackPanel();
        mode.Items.Add("按工参计算近点、中点、远点"); mode.Items.Add("按目标距离反算总下倾角");
        AutomationProperties.SetAutomationId(mode, "CoverageMode");
        mode.Height = 29; mode.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(mode);
        binding.Items.Add("拉线终点绑定远点"); binding.Items.Add("拉线终点绑定中点");
        AutomationProperties.SetAutomationId(binding, "TargetBinding");
        binding.Height = 29; binding.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(binding);
        UniformGrid grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(Field("方位角 / °", "Azimuth", 120)); grid.Children.Add(Field("目标水平距离 / m", "TargetDistance", 859.0875984874681));
        grid.Children.Add(Field("水平波束宽度 / °", "HorizontalBeamwidth", 65)); grid.Children.Add(Field("垂直波束宽度 / °", "VerticalBeamwidth", 8));
        grid.Children.Add(Field("天线中心挂高 / m", "AntennaHeight", 30)); grid.Children.Add(Field("接收点高度 / m", "ReceiverHeight", 0));
        grid.Children.Add(Field("站址地面海拔 / m", "SiteElevation", 0)); grid.Children.Add(Field("目标地面海拔 / m", "TargetElevation", 0));
        grid.Children.Add(Field("当前机械下倾角 / °", "MechanicalTilt", 2)); grid.Children.Add(Field("当前电下倾角 / °", "ElectricalTilt", 4));
        grid.Children.Add(Field("最大显示距离 / m", "DisplayLimit", 5000)); grid.Children.Add(Field("红色边线宽度", "LineWidth", 2));
        panel.Children.Add(grid); return Card("天线与工程参数", panel);
    }

    private UIElement BuildStyle()
    {
        StackPanel panel = new StackPanel();
        transparencyText.Foreground = Muted; panel.Children.Add(transparencyText);
        AutomationProperties.SetAutomationId(transparency, "FillTransparency");
        transparency.Margin = new Thickness(0, 8, 0, 8); panel.Children.Add(transparency); panel.Children.Add(groundBand);
        panel.Children.Add(new TextBlock { Text = "红色边框 · 蓝色填充 · 0% 不透明 / 100% 完全透明", Foreground = Muted,
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
        return Card("扇形样式", panel);
    }

    private UIElement Field(string label, string key, double value)
    {
        TextBox box = new TextBox { Text = value.ToString("G12", CultureInfo.InvariantCulture) };
        AutomationProperties.SetAutomationId(box, key);
        inputs.Add(key, box); box.TextChanged += delegate { QueueChange(); };
        return PlainField(label, box);
    }
    private static UIElement PlainField(string label, TextBox box)
    {
        StackPanel pair = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        pair.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
        box.Height = 29; box.Padding = new Thickness(6, 2, 4, 2); box.VerticalContentAlignment = VerticalAlignment.Center;
        pair.Children.Add(box); return pair;
    }
    private static Border Card(string title, UIElement body)
    {
        StackPanel panel = new StackPanel();
        if (title != null) panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold,
            Foreground = Accent, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(body);
        return new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 227, 235)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10), Child = panel };
    }
    private static UIElement Metric(string label, TextBlock value)
    {
        StackPanel panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 11 });
        value.FontSize = 17; value.FontWeight = FontWeights.SemiBold; value.Foreground = Accent;
        value.Margin = new Thickness(0, 5, 0, 0); value.TextWrapping = TextWrapping.Wrap; panel.Children.Add(value);
        Border card = Card(null, panel); card.Margin = new Thickness(0, 0, 6, 10); return card;
    }
    private static void StyleButton(Button button)
    {
        button.Padding = new Thickness(9, 5, 9, 5); button.Margin = new Thickness(0, 0, 6, 5); button.MinHeight = 29;
    }
    private static Button Button(string text, Action action)
    {
        Button button = new Button { Content = text }; StyleButton(button); button.Click += delegate { action(); }; return button;
    }
    private Button MapButton(string text, Action action)
    { Button button = Button(text, action); button.IsEnabled = false; mapButtons.Add(button); return button; }

    private bool Connected { get { return client != null && client.IsConnected; } }
    private void QueueChange() { if (!loading) { inputTimer.Stop(); inputTimer.Start(); } }
    private void QueueMap()
    {
        if (autoMap.IsChecked == true && Connected && current != null && revision != publishedRevision)
        { mapTimer.Stop(); mapTimer.Start(); }
    }
    private double Read(string key)
    {
        double number = Parse(inputs[key].Text, key);
        inputs[key].ClearValue(Border.BorderBrushProperty); return number;
    }
    private static double Parse(string text, string label)
    {
        double number;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !CoverageMath.Finite(number))
            throw new ArgumentException(label + "请输入有效数字，使用小数点“.”。");
        return number;
    }
    private void Set(string key, double value) { inputs[key].Text = value.ToString("G12", CultureInfo.InvariantCulture); }

    private CoverageParameters ReadParameters()
    {
        CoverageParameters p = new CoverageParameters();
        foreach (FieldInfo field in typeof(CoverageParameters).GetFields())
            if (field.FieldType == typeof(double) && inputs.ContainsKey(field.Name)) field.SetValue(p, Read(field.Name));
        GeoPoint station = new GeoPoint(p.Latitude, p.Longitude);
        if (coordinateType.SelectedIndex == 1) station = CoverageCoordinates.FromGcj02(station);
        p.Latitude = station.Latitude; p.Longitude = station.Longitude;
        p.Mode = (CoverageMode)mode.SelectedIndex; p.Binding = (TargetBinding)binding.SelectedIndex;
        p.Transparency = transparency.Value; p.GroundBandOnly = groundBand.IsChecked == true;
        return p;
    }

    private void Recalculate() { Recalculate(true); }
    private void Recalculate(bool queueMap)
    {
        try
        {
            CoverageParameters p = ReadParameters(); CoverageResult r = CoverageMath.Calculate(p);
            CoverageGeometry g = CoverageMath.BuildGeometry(p, r);
            current = p; calculated = r; geometry = g; revision++;
            binding.IsEnabled = p.Mode == CoverageMode.TargetDistance;
            transparencyText.Text = "蓝色填充透明度：" + p.Transparency.ToString("F0", CultureInfo.InvariantCulture) + "%";
            nearValue.Text = CoverageMath.DistanceText(r.Near); centerValue.Text = CoverageMath.DistanceText(r.Center); farValue.Text = CoverageMath.DistanceText(r.Far);
            tiltValue.Text = "有效高度差 " + r.Height.ToString("F2", CultureInfo.InvariantCulture) + " m　当前总下倾角 " +
                r.CurrentTilt.ToString("F3", CultureInfo.InvariantCulture) + "°" + Environment.NewLine +
                (p.Mode == CoverageMode.Engineering ? "计算总下倾角 " : "目标所需总下倾角 ") + r.Tilt.ToString("F3", CultureInfo.InvariantCulture) +
                "°　方位角 " + CoverageMath.Normalize(p.Azimuth).ToString("F2", CultureInfo.InvariantCulture) + "°";
            bool inside = p.TargetDistance >= r.Near.Value - 0.001 && (!r.Far.HasValue || p.TargetDistance <= r.Far.Value + 0.001);
            targetValue.Text = "终点（WGS84）：" + r.TargetPoint + Environment.NewLine +
                (p.Mode == CoverageMode.Engineering ? "目标参考：" + (inside ? "位于近远点区间内" : "位于近远点区间外") :
                "终点绑定：" + (p.Binding == TargetBinding.Far ? "远点" : "中点") + "；当前机械、电下倾角保持原值");
            notice.Text = r.Notice; notice.Foreground = r.Truncated ? Brushes.DarkOrange : Muted;
            WriteTarget(r.TargetPoint); preview.SetCoverage(p, r, g);
            if (compass != null) compass.Azimuth = p.Azimuth;
            if (calculationError) SetStatus("参数已更新，可以继续操作。", false);
            calculationError = false;
            UpdateButtons(); if (queueMap) QueueMap();
        }
        catch (Exception error)
        {
            current = null; calculated = null; geometry = null; preview.Clear(); mapTimer.Stop();
            calculationError = true;
            nearValue.Text = centerValue.Text = farValue.Text = "—";
            tiltValue.Text = "请检查参数"; targetValue.Text = ""; notice.Text = error.Message; notice.Foreground = Brushes.Firebrick;
            SetStatus(error.Message, true); UpdateButtons();
        }
    }

    private void WriteStation(GeoPoint point)
    {
        if (coordinateType.SelectedIndex == 1) point = CoverageCoordinates.ToGcj02(point);
        Set("Latitude", point.Latitude); Set("Longitude", point.Longitude);
    }
    private void WriteTarget(GeoPoint point)
    {
        if (coordinateType.SelectedIndex == 1) point = CoverageCoordinates.ToGcj02(point);
        targetLatitude.Text = point.Latitude.ToString("F8", CultureInfo.InvariantCulture);
        targetLongitude.Text = point.Longitude.ToString("F8", CultureInfo.InvariantCulture);
    }
    private void CompassAzimuthChanged(double azimuth)
    {
        if (closing || loading || (current != null && Math.Abs(CoverageMath.Normalize(current.Azimuth) - azimuth) < 1e-8)) return;
        loading = true; Set("Azimuth", azimuth); loading = false; QueueChange();
    }
    private void ApplyTargetCoordinates()
    {
        try
        {
            // Keep the user-entered endpoint before recalculation refreshes its display.
            double latitude = Parse(targetLatitude.Text, "终点纬度"), longitude = Parse(targetLongitude.Text, "终点经度");
            RequireParameters();
            GeoPoint target = new GeoPoint(latitude, longitude);
            if (coordinateType.SelectedIndex == 1) target = CoverageCoordinates.FromGcj02(target);
            ApplyTarget(target); SetStatus("已根据终点更新方位角与目标距离。", false);
        }
        catch (Exception error) { SetStatus(error.Message, true); }
    }
    private void ApplyTarget(GeoPoint target)
    {
        GeoLine line = Geodesy.Inverse(ReadParameters().Station, target);
        if (line.Distance < 0.1 || line.Distance > 100000) throw new ArgumentException("拉线水平距离应为 0.1～100000 m，终点不得与站点重合。");
        loading = true; Set("Azimuth", line.Azimuth); Set("TargetDistance", line.Distance); loading = false; Recalculate();
    }
    private async Task PickLineAsync(bool pickStart)
    {
        await RunMapAsync(async delegate
        {
            RequireParameters();
            if (pickStart)
            {
                SetStatus("拉线第 1 步：请在奥维地图单击起点（站点）。", false);
                GeoPoint start = await client.PickAsync(); loading = true; WriteStation(start); loading = false; Recalculate();
            }
            SetStatus("拉线第 2 步：请在奥维地图单击终点。", false);
            GeoPoint target = await client.PickAsync(); ApplyTarget(target);
        }, pickStart ? "请在奥维地图单击拉线起点。" : "请在奥维地图单击拉线终点。", true);
    }

    private void RequireParameters()
    {
        inputTimer.Stop(); Recalculate(false);
        if (current == null) throw new ArgumentException(notice.Text);
    }
    private void SetStatus(string text, bool error) { if (closing) return; status.Text = text; status.Foreground = error ? Brushes.Firebrick : Accent; }
    private void UpdateButtons()
    {
        foreach (Button button in mapButtons) button.IsEnabled = Connected && !busy && current != null;
        connect.IsEnabled = !busy; connect.Content = Connected ? "断开连接" : "连接奥维";
        cancel.IsEnabled = busy && picking && Connected;
        address.IsEnabled = !busy && !Connected; password.IsEnabled = !busy && !Connected;
        autoMap.IsEnabled = Connected && current != null;
    }
    private void Disconnect()
    {
        if (client != null) client.Dispose(); client = null; drawing = null;
        autoMap.IsChecked = false; memberValue.Text = "未连接"; mapTimer.Stop(); UpdateButtons();
    }
    private async Task ConnectAsync()
    {
        if (busy) return;
        if (Connected) { Disconnect(); SetStatus("已断开奥维连接。", false); return; }
        busy = true; UpdateButtons(); SetStatus("正在连接奥维…", false);
        OvitClient candidate = new OvitClient(); client = candidate;
        candidate.Disconnected += delegate(string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!closing && client == candidate) { Disconnect(); SetStatus(message, true); }
            }));
        };
        try
        {
            await candidate.ConnectAsync(address.Text, password.Password);
            Dictionary<string, object> info = await candidate.RequestAsync(20501, OvitJson.Map(), 8000);
            memberValue.Text = "VIP " + Convert.ToString(OvitJson.Get(info, "OmapLevel", "?"), CultureInfo.InvariantCulture);
            int available = Convert.ToInt32(OvitJson.Get(info, "LoadOK", 0), CultureInfo.InvariantCulture);
            drawing = new OvitDrawingSession(candidate); publishedRevision = -1;
            SetStatus(available == 1 ? "奥维已连接，可以选站点、拉线并生成扇形。" : "奥维已连接；请在会员机确认账户及接口权限。", available != 1);
        }
        catch (Exception error) { Disconnect(); SetStatus("连接失败：" + error.Message, true); }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task RunMapAsync(Func<Task> action, string message) { await RunMapAsync(action, message, false); }
    private async Task RunMapAsync(Func<Task> action, string message, bool isPick)
    {
        if (busy) return;
        if (!Connected || drawing == null) { SetStatus("请先连接奥维。", true); return; }
        busy = true; picking = isPick; UpdateButtons(); SetStatus(message, false);
        try { await action(); SetStatus("操作完成。" + (calculated == null ? "" : calculated.Notice), false); }
        catch (Exception error)
        {
            SetStatus(error.Message, true); autoMap.IsChecked = false;
            if (!Connected) Disconnect();
        }
        finally
        {
            busy = false; picking = false; UpdateButtons();
            if (!closing && autoMap.IsChecked == true && revision != publishedRevision) QueueMap();
        }
    }
    private async Task PublishAsync()
    {
        RequireParameters();
        CoverageParameters p = current.Copy(); CoverageGeometry shape = geometry; int version = revision;
        await drawing.UpdateAsync(p, shape); publishedRevision = version;
    }

    private CoverageSettings Settings()
    { return new CoverageSettings { Parameters = current.Copy(), Address = address.Text.Trim(), CoordinateType = coordinateType.SelectedIndex }; }
    private void LoadSettings(CoverageSettings settings)
    {
        loading = true;
        CoverageParameters p = settings.Parameters;
        foreach (FieldInfo field in typeof(CoverageParameters).GetFields())
            if (field.FieldType == typeof(double) && inputs.ContainsKey(field.Name))
                inputs[field.Name].Text = ((double)field.GetValue(p)).ToString("G12", CultureInfo.InvariantCulture);
        coordinateType.SelectedIndex = settings.CoordinateType; WriteStation(p.Station);
        mode.SelectedIndex = (int)p.Mode; binding.SelectedIndex = (int)p.Binding;
        groundBand.IsChecked = p.GroundBandOnly; transparency.Value = p.Transparency;
        if (!Connected) address.Text = settings.Address ?? "ws://127.0.0.1:7699";
        loading = false; inputTimer.Stop(); Recalculate();
    }
    private void SaveParameters()
    {
        try
        {
            RequireParameters();
            SaveFileDialog dialog = new SaveFileDialog { Filter = "天线覆盖参数 (*.xml)|*.xml", FileName = "天线覆盖参数.xml", AddExtension = true };
            if (dialog.ShowDialog(this) == true) { CoverageSettingsStore.Save(dialog.FileName, Settings()); SetStatus("参数已保存：" + dialog.FileName, false); }
        }
        catch (Exception error) { SetStatus(error.Message, true); }
    }
    private void LoadParameters()
    {
        try
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = "天线覆盖参数 (*.xml)|*.xml", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) { LoadSettings(CoverageSettingsStore.Load(dialog.FileName)); SetStatus("已读取参数。", false); }
        }
        catch (Exception error) { SetStatus(error.Message, true); }
    }

    internal static void ShowNotices(Window owner)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Compass.ThirdPartyNotices"))
        using (StreamReader reader = new StreamReader(stream))
        {
            TextBox text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14), BorderThickness = new Thickness(0) };
            new Window { Title = "第三方许可", Width = 650, Height = 480, Content = text, Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        }
    }
}
