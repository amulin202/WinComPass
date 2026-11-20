using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

[assembly: AssemblyTitle("Win桌面罗盘")]
[assembly: AssemblyDescription("Win桌面罗盘")]
[assembly: AssemblyProduct("Win桌面罗盘")]
[assembly: AssemblyCompany("流氓工作室")]
[assembly: AssemblyMetadata("Author", "LmTec")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool coverage = Array.IndexOf(args, "--coverage") >= 0;
        new Application().Run(coverage ? (Window)new CoverageWindow(null) : new CompassForm());
    }
}

internal sealed class CompassForm : Window
{
    private readonly CompassSurface surface;
    private CoverageWindow coverageWindow;
    internal event Action<double> AzimuthChanged;

    public CompassForm()
    {
        Title = "方位角罗盘";
        Width = Height = CompassSurface.DialSize;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        surface = new CompassSurface(this);
        Content = surface;
        ContextMenu menu = new ContextMenu();
        MenuItem coverage = new MenuItem { Header = "天线覆盖…", InputGestureText = "F2" };
        coverage.Click += delegate { ShowCoverage(); };
        menu.Items.Add(coverage);
        ContextMenu = menu;
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F2) { ShowCoverage(); e.Handled = true; }
        };
        Loaded += delegate { surface.Focus(); };
    }

    internal void ShowCoverage()
    {
        if (coverageWindow == null)
        {
            coverageWindow = new CoverageWindow(this) { Owner = this };
            coverageWindow.Closed += delegate { coverageWindow = null; };
        }
        coverageWindow.Show();
        if (coverageWindow.WindowState == WindowState.Minimized) coverageWindow.WindowState = WindowState.Normal;
        coverageWindow.Activate();
    }

    internal void NotifyAzimuthChanged()
    {
        Action<double> changed = AzimuthChanged;
        if (changed != null) changed(Azimuth);
    }

    public double Azimuth
    {
        get { return surface.Azimuth; }
        set { surface.Azimuth = value; }
    }
}

internal sealed class CompassSurface : FrameworkElement
{
    internal const double DialSize = 344;
    private const double Center = DialSize / 2;
    private const double Radius = 146;
    private const double InnerRadius = 93;
    private const double ButtonHalfSize = 10;
    private static readonly Point DialCenter = new Point(Center, Center);
    private static readonly Brush RingFill = Brush(255, 238, 162);
    private static readonly Brush Ink = Brush(35, 32, 27);
    private static readonly Brush Red = Brush(185, 27, 49);
    private static readonly Brush Blue = Brush(20, 78, 163);
    private static readonly Brush LightEdge = Brush(255, 252, 239);
    private static readonly Brush CrosshairEdge = ColorBrush(Color.FromArgb(175, 255, 252, 239));
    private static readonly Brush HitFill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    private static readonly Geometry Ring = CreateRing();
    private static readonly Typeface LabelFace = new Typeface(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly string[] Directions = { "N", "E", "S", "W" };
    private static readonly Point[] Buttons = {
        new Point(Center - 38, Center - 24), new Point(Center + 38, Center - 24),
        new Point(Center + 13, Center - 24), new Point(Center - 13, Center - 24),
        new Point(Center - 38, Center + 24), new Point(Center - 13, Center + 24),
        new Point(Center + 13, Center + 24), new Point(Center + 38, Center + 24)
    };
    private static readonly string[] ButtonNames = {
        "关闭", "移动罗盘", "切换底色", "置顶", "左移", "右移", "上移", "下移"
    };
    private readonly CompassForm window;
    private readonly ToolTip buttonToolTip;
    private double rotation;
    private double lastPointerAngle;
    private bool rotating;
    private bool ringTransparent;
    private int hovered = -1;

    internal CompassSurface(CompassForm window)
    {
        this.window = window;
        buttonToolTip = new ToolTip {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            StaysOpen = true,
            IsHitTestVisible = false
        };
        window.Closed += delegate { buttonToolTip.IsOpen = false; };
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        // WPF preserves edge coverage in the transparent composition surface.
        // No color key, raster asset, or application-created bitmap is involved.
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    internal double Azimuth
    {
        get { return Normalize(-rotation); }
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException("value");
            rotation = -Normalize(value);
            InvalidateVisual();
            window.NotifyAzimuthChanged();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        // Nonzero alpha prevents click-through in otherwise clear parts of the dial.
        drawing.DrawEllipse(HitFill, null, DialCenter, Radius + 3, Radius + 3);
        if (!ringTransparent) drawing.DrawGeometry(RingFill, null, Ring);
        else drawing.DrawEllipse(null, new Pen(LightEdge, 3.8), DialCenter, Radius, Radius);
        Pen minor = new Pen(ringTransparent ? Brushes.Gray : Ink, 1.0);
        Pen halfStep = new Pen(Ink, 1.25);
        Pen decade = new Pen(Ink, 1.5);
        Pen major = new Pen(Ink, 2.0);
        Pen cardinal = new Pen(Ink, 2.6);
        Pen tickOutline = new Pen(LightEdge, 3.6);
        for (int angle = 0; angle < 360; angle++)
        {
            double length;
            Pen pen;
            if (angle % 90 == 0) { length = 24; pen = cardinal; }
            else if (angle % 30 == 0) { length = 16; pen = major; }
            else if (angle % 10 == 0) { length = 12; pen = decade; }
            else if (angle % 5 == 0) { length = 8; pen = halfStep; }
            else { length = 4; pen = minor; }
            Vector direction = (Vector)Polar(1, angle + rotation);
            Point start = DialCenter + direction * (Radius - length);
            Point end = DialCenter + direction * Radius;
            if (ringTransparent && angle % 10 == 0) drawing.DrawLine(tickOutline, start, end);
            drawing.DrawLine(pen, start, end);
        }
        drawing.DrawEllipse(null, new Pen(Ink, 1.5), DialCenter, Radius, Radius);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        for (int i = 0; i < 4; i++)
            DrawLabel(drawing, Directions[i], 16, i == 0 ? Red : Ink, i * 90, 107, dpi);

        for (int angle = 0; angle < 360; angle += 10)
        {
            if (angle % 90 == 0) continue;
            DrawLabel(drawing, angle.ToString(CultureInfo.InvariantCulture), 10.5, Ink, angle, 118, dpi);
        }

        DrawCrosshair(drawing, dpi);
        drawing.DrawGeometry(Red, new Pen(LightEdge, 1.2) { LineJoin = PenLineJoin.Round }, Polygon(
            new Point(Center, Center - Radius - 3),
            new Point(Center - 8, Center - Radius - 17), new Point(Center + 8, Center - Radius - 17)));
        for (int index = 0; index < Buttons.Length; index++)
            DrawButton(drawing, index, dpi);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    private void DrawCrosshair(DrawingContext drawing, DpiScale dpi)
    {
        double width = PhysicalStroke(1.5, dpi.DpiScaleX);
        double height = PhysicalStroke(1.5, dpi.DpiScaleY);
        double crossLeft = PixelEdge(Center - width / 2, dpi.DpiScaleX);
        double crossTop = PixelEdge(Center - height / 2, dpi.DpiScaleY);
        double edgeX = 1 / dpi.DpiScaleX, edgeY = 1 / dpi.DpiScaleY;
        drawing.PushClip(new EllipseGeometry(DialCenter, InnerRadius, InnerRadius));
        // Fill the two arms together so the translucent outline does not darken at their intersection.
        GeometryGroup crossEdge = new GeometryGroup { FillRule = FillRule.Nonzero };
        crossEdge.Children.Add(new RectangleGeometry(new Rect(Center - InnerRadius, crossTop - edgeY,
            InnerRadius * 2, height + 2 * edgeY)));
        crossEdge.Children.Add(new RectangleGeometry(new Rect(crossLeft - edgeX, Center - InnerRadius,
            width + 2 * edgeX, InnerRadius * 2)));
        drawing.DrawGeometry(CrosshairEdge, null, crossEdge);
        const int segmentCount = 20;
        double segmentLength = InnerRadius * 2 / segmentCount;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double start = Center - InnerRadius + segment * segmentLength;
            double end = Center - InnerRadius + (segment + 1) * segmentLength;
            double left = PixelEdge(start, dpi.DpiScaleX), right = PixelEdge(end, dpi.DpiScaleX);
            double top = PixelEdge(start, dpi.DpiScaleY), bottom = PixelEdge(end, dpi.DpiScaleY);
            Brush color = segment % 2 == 0 ? Red : Blue;
            drawing.DrawRectangle(color, null, new Rect(left, crossTop, right - left, height));
            drawing.DrawRectangle(color, null, new Rect(crossLeft, top, width, bottom - top));
        }
        drawing.Pop();
    }

    private static Geometry CreateRing()
    {
        GeometryGroup ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new EllipseGeometry(DialCenter, Radius, Radius));
        ring.Children.Add(new EllipseGeometry(DialCenter, InnerRadius, InnerRadius));
        ring.Freeze();
        return ring;
    }

    private void DrawLabel(DrawingContext drawing, string text, double size, Brush brush,
        double angle, double radius, DpiScale dpi)
    {
        FormattedText label = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, size, brush, null,
            TextFormattingMode.Display, dpi.PixelsPerDip);
        Point position = DialCenter + (Vector)Polar(radius, angle + rotation);
        Point origin = new Point(PixelEdge(position.X - label.Width / 2, dpi.DpiScaleX),
            PixelEdge(position.Y - label.Height / 2, dpi.DpiScaleY));
        if (ringTransparent)
        {
            Pen halo = new Pen(LightEdge, 1.6) { LineJoin = PenLineJoin.Round };
            Geometry outline = label.BuildGeometry(origin);
            drawing.DrawGeometry(null, halo, outline);
            drawing.DrawGeometry(brush, null, outline);
        }
        else drawing.DrawText(label, origin);
    }

    private void DrawButton(DrawingContext drawing, int index, DpiScale dpi)
    {
        Point center = Buttons[index];
        bool active = (index == 2 && ringTransparent) || (index == 3 && window.Topmost);
        Brush face = active ? Brush(218, 233, 253) : hovered == index ? Brush(239, 246, 255) : Brushes.White;
        Brush border = active || hovered == index ? Blue : Brush(111, 120, 132);
        double left = PixelEdge(center.X - ButtonHalfSize, dpi.DpiScaleX);
        double top = PixelEdge(center.Y - ButtonHalfSize, dpi.DpiScaleY);
        double width = PixelEdge(center.X + ButtonHalfSize, dpi.DpiScaleX) - left;
        double height = PixelEdge(center.Y + ButtonHalfSize, dpi.DpiScaleY) - top;
        double edgeX = PhysicalStroke(1, dpi.DpiScaleX), edgeY = PhysicalStroke(1, dpi.DpiScaleY);
        drawing.DrawRoundedRectangle(border, null, new Rect(left, top, width, height), 3.5, 3.5);
        drawing.DrawRoundedRectangle(face, null,
            new Rect(left + edgeX, top + edgeY, width - 2 * edgeX, height - 2 * edgeY),
            3.5 - edgeX, 3.5 - edgeY);

        double iconStroke = PhysicalStroke(1.6, dpi.DpiScaleX);
        double halfStroke = iconStroke / 2;
        double x = PixelEdge(center.X - halfStroke, dpi.DpiScaleX) + halfStroke;
        double y = PixelEdge(center.Y - halfStroke, dpi.DpiScaleY) + halfStroke;
        drawing.PushTransform(new TranslateTransform(x, y));
        Pen ink = new Pen(index == 0 ? Red : Blue, iconStroke);
        ink.StartLineCap = ink.EndLineCap = PenLineCap.Round;
        ink.LineJoin = PenLineJoin.Round;
        if (index == 0)
        {
            drawing.DrawLine(ink, new Point(-4, -4), new Point(4, 4));
            drawing.DrawLine(ink, new Point(-4, 4), new Point(4, -4));
        }
        else if (index == 1)
        {
            for (int i = 0; i < 4; i++) DrawArrow(drawing, ink, i * 90, 0);
        }
        else if (index == 2)
        {
            drawing.DrawEllipse(ringTransparent ? null : RingFill, ink, new Point(), 5, 5);
            if (ringTransparent) drawing.DrawLine(ink, new Point(-4.5, 4.5), new Point(4.5, -4.5));
        }
        else if (index == 3)
        {
            drawing.DrawLine(ink, new Point(-4.5, -5), new Point(4.5, -5));
            drawing.DrawGeometry(ink.Brush, null, Polygon(
                new Point(-3, -5), new Point(3, -5), new Point(2, 1), new Point(-2, 1)));
            drawing.DrawLine(ink, new Point(-4, 1), new Point(4, 1));
            drawing.DrawLine(ink, new Point(0, 1), new Point(0, 6));
        }
        else
        {
            double[] angles = { 270, 90, 0, 180 };
            DrawArrow(drawing, ink, angles[index - 4], 5);
        }
        drawing.Pop();
    }

    private static void DrawArrow(DrawingContext drawing, Pen pen, double angle, double tail)
    {
        drawing.PushTransform(new RotateTransform(angle));
        drawing.DrawLine(pen, new Point(0, tail), new Point(0, -6));
        drawing.DrawLine(pen, new Point(0, -6), new Point(-2.7, -3));
        drawing.DrawLine(pen, new Point(0, -6), new Point(2.7, -3));
        drawing.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        buttonToolTip.IsOpen = false;
        Focus();
        Point point = e.GetPosition(this);
        int button = HitButton(point);
        if (button >= 0)
        {
            e.Handled = true;
            switch (button)
            {
                case 0: window.Close(); return;
                case 1: window.DragMove(); return;
                case 2: ringTransparent = !ringTransparent; break;
                case 3: window.Topmost = !window.Topmost; break;
                case 4: MoveWindow(-1, 0); break;
                case 5: MoveWindow(1, 0); break;
                case 6: MoveWindow(0, -1); break;
                case 7: MoveWindow(0, 1); break;
            }
            InvalidateVisual();
        }
        else if (HitDial(point))
        {
            rotating = CaptureMouse();
            lastPointerAngle = PointerAngle(point);
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);
        if (rotating)
        {
            double angle = PointerAngle(point);
            double delta = angle - lastPointerAngle;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            rotation += delta;
            lastPointerAngle = angle;
            InvalidateVisual();
            window.NotifyAzimuthChanged();
            return;
        }
        int next = HitButton(point);
        if (hovered != next)
        {
            hovered = next;
            buttonToolTip.IsOpen = false;
            if (next >= 0)
            {
                buttonToolTip.Content = ButtonNames[next];
                buttonToolTip.HorizontalOffset = Buttons[next].X + 12;
                buttonToolTip.VerticalOffset = Buttons[next].Y + 12;
                buttonToolTip.IsOpen = true;
            }
            InvalidateVisual();
        }
        Cursor = next == 1 ? Cursors.SizeAll : next >= 0 || HitDial(point) ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!rotating) return;
        rotating = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        rotating = false;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (rotating) return;
        hovered = -1;
        buttonToolTip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape: window.Close(); break;
            case Key.Left: Azimuth -= step; break;
            case Key.Right: Azimuth += step; break;
            case Key.Home: Azimuth = 0; break;
            default: return;
        }
        e.Handled = true;
    }

    private void MoveWindow(double x, double y)
    {
        Rect area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left - window.Width + 30,
            Math.Min(area.Right - 30, window.Left + x));
        window.Top = Math.Max(area.Top - window.Height + 30,
            Math.Min(area.Bottom - 30, window.Top + y));
    }

    private static int HitButton(Point point)
    {
        for (int i = 0; i < Buttons.Length; i++)
        {
            Vector offset = point - Buttons[i];
            if (Math.Abs(offset.X) <= ButtonHalfSize && Math.Abs(offset.Y) <= ButtonHalfSize) return i;
        }
        return -1;
    }

    private static bool HitDial(Point point)
    {
        double squared = (point - DialCenter).LengthSquared;
        return squared <= (Radius + 3) * (Radius + 3);
    }

    private static double PointerAngle(Point point)
    {
        return Math.Atan2(point.Y - Center, point.X - Center) * 180 / Math.PI + 90;
    }

    private static double Normalize(double angle)
    {
        return (angle % 360 + 360) % 360;
    }

    private static Point Polar(double radius, double degrees)
    {
        double radians = (degrees - 90) * Math.PI / 180;
        return new Point(Math.Cos(radians) * radius, Math.Sin(radians) * radius);
    }

    private static double PhysicalStroke(double dips, double scale)
    {
        return Math.Max(1, Math.Round(dips * scale, MidpointRounding.AwayFromZero)) / scale;
    }

    private static double PixelEdge(double coordinate, double scale)
    {
        return Math.Round(coordinate * scale) / scale;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        return ColorBrush(Color.FromRgb(r, g, b));
    }

    private static SolidColorBrush ColorBrush(Color color)
    {
        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry Polygon(params Point[] points)
    {
        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++) context.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
