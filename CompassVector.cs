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
[assembly: AssemblyVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        new Application().Run(new CompassForm());
    }
}

internal sealed class CompassForm : Window
{
    private readonly CompassSurface surface;

    public CompassForm()
    {
        Title = "方位角罗盘";
        Width = Height = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        surface = new CompassSurface(this);
        Content = surface;
        Loaded += delegate { surface.Focus(); };
    }

    public double Azimuth
    {
        get { return surface.Azimuth; }
        set { surface.Azimuth = value; }
    }
}

internal sealed class CompassSurface : FrameworkElement
{
    private const double Center = 150;
    private const double Radius = 125;
    private const double InnerRadius = 83;
    private static readonly Geometry[] DigitOutlines = CreateDigitOutlines();
    private static readonly Typeface LabelFace = new Typeface(
        new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Point[] Buttons = {
        new Point(120, 130), new Point(180, 130), new Point(164, 130), new Point(136, 130),
        new Point(120, 170), new Point(136, 170), new Point(164, 170), new Point(180, 170)
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
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        // Nonzero alpha prevents click-through in otherwise clear parts of the dial.
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null,
            new Point(Center, Center), Radius + 3, Radius + 3);
        drawing.PushTransform(new TranslateTransform(Center, Center));
        drawing.PushTransform(new RotateTransform(rotation));
        if (!ringTransparent)
        {
            Geometry ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(), Radius, Radius),
                new EllipseGeometry(new Point(), InnerRadius, InnerRadius));
            drawing.DrawGeometry(Brushes.Yellow, null, ring);
        }
        drawing.DrawEllipse(null, new Pen(Brushes.Black, StrokeThickness), new Point(), Radius, Radius);
        for (int angle = 0; angle < 360; angle++)
        {
            double length = 3;
            if (angle % 5 == 0) length = 6;
            if (angle % 10 == 0) length = 10;
            if (angle % 30 == 0) length = 16;
            if (angle % 90 == 0) length = 22;
            drawing.DrawLine(new Pen(Brushes.Black, StrokeThickness),
                Polar(Radius - length, angle), Polar(Radius, angle));
        }
        string[] directions = { "N", "E", "S", "W" };
        for (int i = 0; i < 4; i++)
            DrawLabel(drawing, directions[i], 14, i == 0 ? Brushes.Red : Brushes.Black,
                i * 90, 75, i * 90);
        drawing.Pop();
        drawing.Pop();

        for (int angle = 0; angle < 360; angle += 10)
        {
            if (angle % 90 == 0) continue;
            DrawNumericLabel(drawing, angle);
        }

        Pen red = new Pen(Brushes.Red, StrokeThickness);
        Pen blue = new Pen(Brushes.Blue, StrokeThickness);
        double crossCenter = SnapStroke(Center);
        drawing.PushClip(new EllipseGeometry(new Point(Center, Center), InnerRadius, InnerRadius));
        const int segmentCount = 20;
        double segmentLength = InnerRadius * 2 / segmentCount;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double start = Center - InnerRadius + segment * segmentLength;
            double end = Center - InnerRadius + (segment + 1) * segmentLength;
            Pen color = segment % 2 == 0 ? red : blue;
            drawing.DrawLine(color, new Point(start, crossCenter), new Point(end, crossCenter));
            drawing.DrawLine(color, new Point(crossCenter, start), new Point(crossCenter, end));
        }
        drawing.Pop();
        drawing.DrawGeometry(Brushes.Red, null, Polygon(
            new Point(150, 23), new Point(143, 9), new Point(157, 9)));
        for (int index = 0; index < Buttons.Length; index++)
            DrawButton(drawing, index);
    }

    private static Geometry[] CreateDigitOutlines()
    {
        // Filled vector outlines on a 3-by-5 design grid, not image resources.
        string[] paths = {
            "M0,0 H3 V5 H0 Z M1,1 H2 V4 H1 Z",
            "M0,0 H1 V5 H0 Z",
            "M0,0 H3 V3 H1 V4 H3 V5 H0 V2 H2 V1 H0 Z",
            "M0,0 H3 V5 H0 V4 H2 V3 H0 V2 H2 V1 H0 Z",
            "M0,0 H1 V2 H2 V0 H3 V5 H2 V3 H0 Z",
            "M0,0 H3 V1 H1 V2 H3 V5 H0 V4 H2 V3 H0 Z",
            "M0,0 H3 V1 H1 V2 H3 V5 H0 Z M1,3 H2 V4 H1 Z",
            "M0,0 H3 V5 H2 V1 H0 Z",
            "M0,0 H3 V5 H0 Z M1,1 H2 V2 H1 Z M1,3 H2 V4 H1 Z",
            "M0,0 H3 V5 H0 V4 H2 V3 H0 Z M1,1 H2 V2 H1 Z"
        };
        Geometry[] outlines = new Geometry[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            outlines[i] = Geometry.Parse(paths[i]).GetFlattenedPathGeometry();
            outlines[i].Freeze();
        }
        return outlines;
    }

    private void DrawNumericLabel(DrawingContext drawing, int angle)
    {
        string text = angle.ToString(CultureInfo.InvariantCulture);
        double width = text.Length - 1;
        foreach (char digit in text) width += digit == '1' ? 1 : 3;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point position = Polar(99, angle + rotation);
        // Fit both the origin and every contour edge to physical pixels.
        double left = Math.Round((Center + position.X - width / 2) * dpi.DpiScaleX) / dpi.DpiScaleX;
        double top = Math.Round((Center + position.Y - 2.5) * dpi.DpiScaleY) / dpi.DpiScaleY;
        drawing.PushTransform(new TranslateTransform(left, top));
        StreamGeometry fitted = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (StreamGeometryContext context = fitted.Open())
        {
            double offset = 0;
            foreach (char digit in text)
            {
                PathGeometry outline = (PathGeometry)DigitOutlines[digit - '0'];
                foreach (PathFigure figure in outline.Figures)
                {
                    context.BeginFigure(FitDigitPoint(figure.StartPoint, offset, dpi),
                        figure.IsFilled, figure.IsClosed);
                    foreach (PathSegment segment in figure.Segments)
                    {
                        LineSegment line = segment as LineSegment;
                        if (line != null)
                            context.LineTo(FitDigitPoint(line.Point, offset, dpi), true, false);
                        else
                        {
                            PolyLineSegment polyline = (PolyLineSegment)segment;
                            foreach (Point point in polyline.Points)
                                context.LineTo(FitDigitPoint(point, offset, dpi), true, false);
                        }
                    }
                }
                offset += digit == '1' ? 2 : 4;
            }
        }
        fitted.Freeze();
        drawing.DrawGeometry(Brushes.Black, null, fitted);
        drawing.Pop();
    }

    private static Point FitDigitPoint(Point point, double offset, DpiScale dpi)
    {
        return new Point(
            Math.Round((point.X + offset) * dpi.DpiScaleX, MidpointRounding.AwayFromZero) / dpi.DpiScaleX,
            Math.Round(point.Y * dpi.DpiScaleY, MidpointRounding.AwayFromZero) / dpi.DpiScaleY);
    }

    private void DrawLabel(DrawingContext drawing, string text, double size,
                                  Brush brush, double angle, double radius, double textRotation)
    {
        Point position = Polar(radius, angle);
        drawing.PushTransform(new TranslateTransform(position.X, position.Y));
        drawing.PushTransform(new RotateTransform(textRotation));
        FormattedText label = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelFace, size, brush, null,
            TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(label, new Point(-label.Width / 2, -label.Height / 2));
        drawing.Pop();
        drawing.Pop();
    }

    private void DrawButton(DrawingContext drawing, int index)
    {
        Point center = Buttons[index];
        bool active = (index == 2 && ringTransparent) || (index == 3 && window.Topmost);
        Brush face = active ? Brush(210, 230, 255) : Brushes.White;
        Brush border = active ? Brush(0, 70, 160) : hovered == index ? Brushes.Black : Brush(96, 96, 96);
        double left = SnapStroke(center.X - 7.5), top = SnapStroke(center.Y - 7.5);
        drawing.DrawRectangle(face, new Pen(border, StrokeThickness), new Rect(left, top,
            SnapStroke(center.X + 7.5) - left, SnapStroke(center.Y + 7.5) - top));
        drawing.PushTransform(new TranslateTransform(SnapStroke(center.X), SnapStroke(center.Y)));
        Pen ink = new Pen(index == 0 ? Brushes.Red : Brush(0, 45, 110), StrokeThickness);
        ink.StartLineCap = ink.EndLineCap = PenLineCap.Round;
        ink.LineJoin = PenLineJoin.Round;
        if (index == 0)
        {
            drawing.DrawLine(ink, new Point(-3.4, -3.4), new Point(3.4, 3.4));
            drawing.DrawLine(ink, new Point(-3.4, 3.4), new Point(3.4, -3.4));
        }
        else if (index == 1)
        {
            DrawArrow(drawing, ink, 0, 5);
            DrawArrow(drawing, ink, 90, 5);
            DrawArrow(drawing, ink, 180, 5);
            DrawArrow(drawing, ink, 270, 5);
        }
        else if (index == 2)
        {
            drawing.DrawEllipse(ringTransparent ? null : Brushes.Yellow,
                new Pen(Brushes.Black, StrokeThickness), new Point(), 4.5, 4.5);
            if (ringTransparent) drawing.DrawLine(ink, new Point(-4, 4), new Point(4, -4));
        }
        else if (index == 3)
        {
            drawing.DrawLine(ink, new Point(-4, -4), new Point(4, -4));
            drawing.DrawGeometry(ink.Brush, null, Polygon(
                new Point(-3, -3), new Point(3, -3), new Point(1.8, 1), new Point(-1.8, 1)));
            drawing.DrawLine(ink, new Point(0, 1), new Point(0, 5));
        }
        else
        {
            double[] angles = { 270, 90, 0, 180 };
            DrawArrow(drawing, ink, angles[index - 4], 5);
        }
        drawing.Pop();
    }

    private static void DrawArrow(DrawingContext drawing, Pen pen, double angle, double length)
    {
        drawing.PushTransform(new RotateTransform(angle));
        drawing.DrawLine(pen, new Point(0, length - 4), new Point(0, -length));
        drawing.DrawLine(pen, new Point(0, -length), new Point(-2.5, -length + 3));
        drawing.DrawLine(pen, new Point(0, -length), new Point(2.5, -length + 3));
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
            if ((point - Buttons[i]).LengthSquared <= 8.5 * 8.5) return i;
        return -1;
    }

    private static bool HitDial(Point point)
    {
        double squared = (point - new Point(Center, Center)).LengthSquared;
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

    private double StrokeThickness
    {
        get
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return Math.Max(1, Math.Round(scale)) / scale;
        }
    }

    private double SnapStroke(double coordinate)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double halfPixels = StrokeThickness * scale / 2;
        return (Math.Round(coordinate * scale - halfPixels) + halfPixels) / scale;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
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
