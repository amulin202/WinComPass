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
[assembly: AssemblyVersion(Program.Version + ".0")]
[assembly: AssemblyFileVersion(Program.Version + ".0")]
[assembly: AssemblyInformationalVersion(Program.Version)]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class Program
{
    internal const string Version = "1.3.0";

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
        Width = CompassSurface.DialWidth;
        Height = CompassSurface.DialHeight;
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
}

internal sealed class CompassSurface : FrameworkElement
{
    internal const double DialWidth = 251;
    internal const double DialHeight = 249;
    private const double CenterX = DialWidth / 2;
    private const double CenterY = DialHeight / 2;
    private const double Radius = 121;
    private const double InnerRadius = 90;
    private const double CrosshairRadius = 86;
    private const double CrosshairGap = 10;
    private const double ButtonHitHalfSize = 10;
    private const double ButtonFaceHalfSize = 7.5;
    private static readonly Point DialCenter = new Point(CenterX, CenterY);
    private static readonly Brush RingFill = Brush(252, 254, 4);
    private static readonly Brush RingEdge = Brush(146, 137, 53);
    private static readonly Brush PointerInk = Brush(171, 35, 45);
    private static readonly Brush CardinalInk = Brush(227, 66, 52);
    private static readonly Brush ButtonSwatch = Brush(252, 254, 4);
    private static readonly Brush Ink = Brushes.Black;
    // A second, partially opaque pass darkens antialiased pixels without widening the path.
    private static readonly Brush InkBoost = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
    private static readonly Brush Red = Brush(185, 27, 49);
    private static readonly Brush Blue = Brush(20, 78, 163);
    private static readonly Brush GraphiteGlyph = Brush(224, 234, 232);
    private static readonly Brush GraphiteClose = Brush(242, 161, 148);
    private static readonly Brush LightEdge = Brush(255, 252, 239);
    private static readonly Brush HitFill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    private static readonly Geometry Ring = CreateRing();
    private static readonly Typeface NumberFace = new Typeface(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen NumberHalo = new Pen(LightEdge, 0.8) { LineJoin = PenLineJoin.Round };
    private static readonly Point[] Buttons = {
        new Point(CenterX - 37, CenterY - 18), new Point(CenterX + 37, CenterY - 18),
        new Point(CenterX + 15, CenterY - 18), new Point(CenterX - 15, CenterY - 18),
        new Point(CenterX - 37, CenterY + 18), new Point(CenterX - 15, CenterY + 18),
        new Point(CenterX + 15, CenterY + 18), new Point(CenterX + 37, CenterY + 18)
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
        ContextMenu = new ContextMenu();
        ContextMenu.Opened += delegate { buttonToolTip.IsOpen = false; };
        MenuItem about = new MenuItem { Header = "关于" };
        about.Click += delegate { ShowAbout(); };
        ContextMenu.Items.Add(about);
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        // WPF preserves edge coverage in the transparent composition surface.
        // No color key, raster asset, or application-created bitmap is involved.
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    private void ShowAbout()
    {
        buttonToolTip.IsOpen = false;
        MessageBox.Show(window,
            "Win桌面罗盘  " + Program.Version + "（C# / WPF）\n" +
            "作者：LmTec · 流氓工作室\n\n" +
            "使用说明\n" +
            "把中心留空的十字线对准底图上的测量点，读取外圈顺时针角度。\n" +
            "拖动表盘旋转刻度，或拖动移动按钮来移动窗口。\n" +
            "盘内按钮还可切换底色、切换置顶、微移或关闭窗口。\n\n" +
            "快捷键\n" +
            "← / →：旋转 1°，按住 Shift 时旋转 10°\n" +
            "Home：方位角归零\n" +
            "Alt + 方向键：微移窗口 1 DIP\n" +
            "Esc：关闭罗盘",
            "关于 Win桌面罗盘", MessageBoxButton.OK, MessageBoxImage.Information);
        Focus();
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
        drawing.DrawEllipse(HitFill, null, DialCenter, Radius + 3, Radius + 3);
        if (!ringTransparent)
        {
            drawing.DrawGeometry(RingFill, null, Ring);
            drawing.DrawEllipse(null, new Pen(RingEdge, 0.6), DialCenter, Radius, Radius);
        }
        else drawing.DrawEllipse(null, new Pen(LightEdge, 1.8), DialCenter, Radius, Radius);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        DrawTicks(drawing, dpi);
        if (ringTransparent) drawing.DrawEllipse(null, new Pen(Ink, 0.7), DialCenter, Radius, Radius);

        for (int angle = 0; angle < 360; angle += 10)
            DrawLabel(drawing, angle, dpi);

        DrawInnerTickExtensions(drawing);
        DrawCrosshair(drawing, dpi);
        drawing.DrawGeometry(PointerInk, null, Polygon(
            new Point(CenterX, CenterY - Radius + 1),
            new Point(CenterX - 2.5, CenterY - Radius - 2.5), new Point(CenterX + 2.5, CenterY - Radius - 2.5)));
        for (int index = 0; index < Buttons.Length; index++)
            DrawButton(drawing, index, dpi);
    }

    private void DrawTicks(DrawingContext drawing, DpiScale dpi)
    {
        Pen halfStep = new Pen(Ink, 0.95);
        Pen decade = new Pen(Ink, 1.05);
        Pen halfStepBoost = new Pen(InkBoost, halfStep.Thickness);
        Pen decadeBoost = new Pen(InkBoost, decade.Thickness);
        Pen tickOutline = new Pen(LightEdge, 1.85);
        Pen fineTickOutline = new Pen(LightEdge, 1.75);
        Pen cardinal = new Pen(CardinalInk, 1.55);
        Pen cardinalOutline = new Pen(LightEdge, 2.35);
        for (int angle = 0; angle < 360; angle += 5)
        {
            bool mainDirection = angle % 90 == 0;
            bool numbered = angle % 10 == 0;
            double length = mainDirection ? 11 : numbered ? 9 : 5;
            Vector direction = (Vector)Polar(1, angle + rotation);
            Point start = DialCenter + direction * (Radius - length);
            Point end = DialCenter + direction * Radius;
            if (ringTransparent)
                DrawTick(drawing, mainDirection ? cardinalOutline : numbered ? tickOutline : fineTickOutline, start, end, dpi);
            DrawTick(drawing, mainDirection ? cardinal : numbered ? decade : halfStep, start, end, dpi);
            if (!mainDirection)
                DrawTick(drawing, numbered ? decadeBoost : halfStepBoost, start, end, dpi);
        }
    }

    private void DrawInnerTickExtensions(DrawingContext drawing)
    {
        const double extensionLength = 24;
        const double gap = 2;
        Pen ink = new Pen(Ink, 1.15);
        Pen boost = new Pen(InkBoost, ink.Thickness);
        for (int angle = 0; angle < 360; angle += 10)
        {
            Vector direction = (Vector)Polar(1, angle + rotation);
            // The fixed crosshair already covers the screen axes.
            if (Math.Abs(direction.X) < 0.0001 || Math.Abs(direction.Y) < 0.0001) continue;
            Point start = DialCenter + direction * (InnerRadius - gap - extensionLength);
            Point end = DialCenter + direction * (InnerRadius - gap);
            drawing.DrawLine(ink, start, end);
            drawing.DrawLine(boost, start, end);
        }
    }

    private static void DrawTick(DrawingContext drawing, Pen pen, Point start, Point end, DpiScale dpi)
    {
        const double axisTolerance = 0.0001;
        if (Math.Abs(start.X - end.X) < axisTolerance)
        {
            double width = PhysicalStroke(pen.Thickness, dpi.DpiScaleX);
            double top = PixelEdge(Math.Min(start.Y, end.Y), dpi.DpiScaleY);
            double bottom = PixelEdge(Math.Max(start.Y, end.Y), dpi.DpiScaleY);
            drawing.DrawRectangle(pen.Brush, null,
                new Rect(PixelEdge(start.X - width / 2, dpi.DpiScaleX), top, width, bottom - top));
        }
        else if (Math.Abs(start.Y - end.Y) < axisTolerance)
        {
            double height = PhysicalStroke(pen.Thickness, dpi.DpiScaleY);
            double left = PixelEdge(Math.Min(start.X, end.X), dpi.DpiScaleX);
            double right = PixelEdge(Math.Max(start.X, end.X), dpi.DpiScaleX);
            drawing.DrawRectangle(pen.Brush, null,
                new Rect(left, PixelEdge(start.Y - height / 2, dpi.DpiScaleY), right - left, height));
        }
        else drawing.DrawLine(pen, start, end);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    private void DrawCrosshair(DrawingContext drawing, DpiScale dpi)
    {
        double width = PhysicalStroke(1, dpi.DpiScaleX);
        double height = PhysicalStroke(1, dpi.DpiScaleY);
        double crossLeft = PixelEdge(CenterX - width / 2, dpi.DpiScaleX);
        double crossTop = PixelEdge(CenterY - height / 2, dpi.DpiScaleY);
        double gapWidth = PhysicalStroke(CrosshairGap, dpi.DpiScaleX);
        double gapHeight = PhysicalStroke(CrosshairGap, dpi.DpiScaleY);
        double gapLeft = PixelEdge(CenterX - gapWidth / 2, dpi.DpiScaleX);
        double gapTop = PixelEdge(CenterY - gapHeight / 2, dpi.DpiScaleY);
        drawing.PushClip(new EllipseGeometry(DialCenter, CrosshairRadius, CrosshairRadius));
        const int segmentCount = 20;
        double segmentLength = CrosshairRadius * 2 / segmentCount;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double start = -CrosshairRadius + segment * segmentLength;
            double end = -CrosshairRadius + (segment + 1) * segmentLength;
            double left = PixelEdge(CenterX + start, dpi.DpiScaleX), right = PixelEdge(CenterX + end, dpi.DpiScaleX);
            double top = PixelEdge(CenterY + start, dpi.DpiScaleY), bottom = PixelEdge(CenterY + end, dpi.DpiScaleY);
            if (segment < segmentCount / 2)
            {
                right = Math.Min(right, gapLeft);
                bottom = Math.Min(bottom, gapTop);
            }
            else
            {
                left = Math.Max(left, gapLeft + gapWidth);
                top = Math.Max(top, gapTop + gapHeight);
            }
            Brush color = segment % 2 == 0 ? Red : Blue;
            if (right > left) drawing.DrawRectangle(color, null, new Rect(left, crossTop, right - left, height));
            if (bottom > top) drawing.DrawRectangle(color, null, new Rect(crossLeft, top, width, bottom - top));
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

    private void DrawLabel(DrawingContext drawing, int angle, DpiScale dpi)
    {
        FormattedText label = new FormattedText(angle.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NumberFace, 8, Ink, null,
            TextFormattingMode.Display, dpi.PixelsPerDip);
        Geometry glyph = label.BuildGeometry(new Point());
        Rect bounds = glyph.Bounds;
        Point position = DialCenter + (Vector)Polar(99.8, angle + rotation);
        Point origin = new Point(PixelEdge(position.X - bounds.X - bounds.Width / 2, dpi.DpiScaleX),
            PixelEdge(position.Y - bounds.Y - bounds.Height / 2, dpi.DpiScaleY));
        if (ringTransparent)
        {
            // Outline and fill use the exact same shape so they cannot drift apart.
            drawing.PushTransform(new TranslateTransform(origin.X, origin.Y));
            drawing.DrawGeometry(LightEdge, NumberHalo, glyph);
            drawing.DrawGeometry(Ink, null, glyph);
            drawing.DrawGeometry(InkBoost, null, glyph);
            drawing.Pop();
        }
        else
        {
            drawing.DrawText(label, origin);
            label.SetForegroundBrush(InkBoost);
            drawing.DrawText(label, origin);
        }
    }

    private void DrawButton(DrawingContext drawing, int index, DpiScale dpi)
    {
        Point center = Buttons[index];
        bool active = (index == 2 && ringTransparent) || (index == 3 && window.Topmost);
        bool over = hovered == index;
        Brush face = ringTransparent
            ? over ? Brush(62, 80, 77) : active ? Brush(49, 67, 64) : Brush(30, 39, 38)
            : active ? Brush(219, 233, 248) : over ? Brush(249, 251, 254) : Brush(240, 240, 240);
        Brush border = ringTransparent
            ? over ? Brush(149, 179, 171) : active ? Brush(112, 154, 145) : Brush(87, 103, 100)
            : active || over ? Brush(114, 145, 184) : Brush(185, 190, 196);
        Brush glyph = ringTransparent ? GraphiteGlyph : Blue;
        double left = PixelEdge(center.X - ButtonFaceHalfSize, dpi.DpiScaleX);
        double top = PixelEdge(center.Y - ButtonFaceHalfSize, dpi.DpiScaleY);
        double width = PixelEdge(center.X + ButtonFaceHalfSize, dpi.DpiScaleX) - left;
        double height = PixelEdge(center.Y + ButtonFaceHalfSize, dpi.DpiScaleY) - top;
        double edgeX = PhysicalStroke(1, dpi.DpiScaleX), edgeY = PhysicalStroke(1, dpi.DpiScaleY);
        const double corner = 1.5;
        drawing.DrawRoundedRectangle(border, null, new Rect(left, top, width, height), corner, corner);
        drawing.DrawRoundedRectangle(face, null,
            new Rect(left + edgeX, top + edgeY, width - 2 * edgeX, height - 2 * edgeY),
            corner - edgeX, corner - edgeY);

        Point iconCenter = new Point(left + width / 2, top + height / 2);
        double x = iconCenter.X;
        double y = iconCenter.Y;
        if (index == 0)
        {
            const double outer = 4.75, inner = 3.4, join = outer - inner;
            drawing.DrawGeometry(ringTransparent ? GraphiteClose : Red, null, Polygon(
                new Point(x - outer, y - inner), new Point(x - inner, y - outer),
                new Point(x, y - join), new Point(x + inner, y - outer), new Point(x + outer, y - inner),
                new Point(x + join, y), new Point(x + outer, y + inner), new Point(x + inner, y + outer),
                new Point(x, y + join), new Point(x - inner, y + outer), new Point(x - outer, y + inner),
                new Point(x - join, y)));
        }
        else if (index == 1)
        {
            for (int i = 0; i < 4; i++) DrawArrow(drawing, iconCenter, dpi, i, true, glyph);
        }
        else if (index == 2)
        {
            drawing.DrawEllipse(glyph, null, iconCenter, 5.4, 5.4);
            drawing.DrawEllipse(ringTransparent ? face : ButtonSwatch, null, iconCenter, 3.65, 3.65);
            if (ringTransparent)
                drawing.DrawLine(new Pen(glyph, 1.6), new Point(x - 3.7, y + 3.7), new Point(x + 3.7, y - 3.7));
        }
        else if (index == 3)
        {
            DrawIconBar(drawing, dpi, glyph, x - 4, y - 5.5, x + 4, y - 3.5);
            drawing.DrawGeometry(glyph, null, Polygon(
                new Point(x - 2.75, y - 4.5), new Point(x + 2.75, y - 4.5),
                new Point(x + 1.8, y + 1), new Point(x - 1.8, y + 1)));
            DrawIconBar(drawing, dpi, glyph, x - 3.7, y, x + 3.7, y + 2);
            double needle = PhysicalStroke(1, dpi.DpiScaleX);
            double needleLeft = PixelEdge(x - needle / 2, dpi.DpiScaleX);
            DrawIconBar(drawing, dpi, glyph, needleLeft, y + 1, needleLeft + needle, y + 5.5);
        }
        else
        {
            int direction = index == 4 ? 3 : index == 5 ? 1 : index == 6 ? 0 : 2;
            DrawArrow(drawing, iconCenter, dpi, direction, false, glyph);
        }
    }

    private static void DrawIconBar(DrawingContext drawing, DpiScale dpi, Brush glyph,
        double left, double top, double right, double bottom)
    {
        Point start = new Point(PixelEdge(left, dpi.DpiScaleX), PixelEdge(top, dpi.DpiScaleY));
        Point end = new Point(PixelEdge(right, dpi.DpiScaleX), PixelEdge(bottom, dpi.DpiScaleY));
        drawing.DrawRectangle(glyph, null, new Rect(start, end));
    }

    private static void DrawArrow(DrawingContext drawing, Point center, DpiScale dpi,
        int direction, bool fourWay, Brush glyph)
    {
        // Direction starts at up and increases clockwise. Align straight edges before filling the arrow.
        bool vertical = direction % 2 == 0;
        int sign = direction == 0 || direction == 3 ? -1 : 1;
        double forwardCenter = vertical ? center.Y : center.X;
        double forwardScale = vertical ? dpi.DpiScaleY : dpi.DpiScaleX;
        double sideCenter = vertical ? center.X : center.Y;
        double sideScale = vertical ? dpi.DpiScaleX : dpi.DpiScaleY;
        double shaftWidth = PhysicalStroke(fourWay ? 1.4 : 1.7, sideScale);
        double shaftLeft = PixelEdge(sideCenter - shaftWidth / 2, sideScale);
        double shaftRight = shaftLeft + shaftWidth;
        double wing = PhysicalStroke(fourWay ? 1.5 : 2, sideScale);
        double tip = PixelEdge(forwardCenter + sign * 5.5, forwardScale);
        double shoulder = PixelEdge(forwardCenter + sign * (fourWay ? 3 : 1.5), forwardScale);
        double tail = PixelEdge(forwardCenter - sign * (fourWay ? 1 : 5), forwardScale);
        drawing.DrawGeometry(glyph, null, Polygon(
            ArrowPoint(vertical, tail, shaftLeft), ArrowPoint(vertical, shoulder, shaftLeft),
            ArrowPoint(vertical, shoulder, shaftLeft - wing), ArrowPoint(vertical, tip, (shaftLeft + shaftRight) / 2),
            ArrowPoint(vertical, shoulder, shaftRight + wing), ArrowPoint(vertical, shoulder, shaftRight),
            ArrowPoint(vertical, tail, shaftRight)));
    }

    private static Point ArrowPoint(bool vertical, double forward, double side)
    {
        return vertical ? new Point(side, forward) : new Point(forward, side);
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
        ModifierKeys modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            switch (e.Key == Key.System ? e.SystemKey : e.Key)
            {
                case Key.Left: MoveWindow(-1, 0); break;
                case Key.Right: MoveWindow(1, 0); break;
                case Key.Up: MoveWindow(0, -1); break;
                case Key.Down: MoveWindow(0, 1); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        double step = (modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
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
            if (Math.Abs(offset.X) <= ButtonHitHalfSize && Math.Abs(offset.Y) <= ButtonHitHalfSize) return i;
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
        return Math.Atan2(point.Y - CenterY, point.X - CenterX) * 180 / Math.PI + 90;
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
