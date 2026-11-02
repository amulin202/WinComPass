using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

internal sealed class CoveragePreview : FrameworkElement
{
    private CoverageParameters parameters;
    private CoverageResult result;
    private CoverageGeometry geometry;
    private bool dragging;
    private double scale, dragScale;
    private Point origin;
    internal event Action<double, double> TargetChanged;

    internal CoveragePreview()
    {
        MinHeight = 300;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        ToolTip = "在预览中点击或拖动设置目标方向与距离；奥维地图取点使用左侧按钮。";
    }

    internal void SetCoverage(CoverageParameters p, CoverageResult r, CoverageGeometry g)
    { parameters = p; result = r; geometry = g; InvalidateVisual(); }

    internal void Clear() { parameters = null; result = null; geometry = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        origin = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 54);
        scale = dragging ? dragScale : result == null ? 1 : radius / Math.Max(1, result.DisplayRadius);
        Pen grid = new Pen(new SolidColorBrush(Color.FromRgb(227, 233, 240)), 1);
        for (int i = 1; i <= 4; i++)
        {
            double ring = radius * i / 4;
            dc.DrawEllipse(null, grid, origin, ring, ring);
        }
        dc.DrawLine(grid, new Point(origin.X - radius, origin.Y), new Point(origin.X + radius, origin.Y));
        dc.DrawLine(grid, new Point(origin.X, origin.Y - radius), new Point(origin.X, origin.Y + radius));
        Label(dc, "N", new Point(origin.X, origin.Y - radius - 25), Brushes.DimGray, 13, true);
        Label(dc, "S", new Point(origin.X, origin.Y + radius + 15), Brushes.DimGray, 12, true);
        Label(dc, "W", new Point(origin.X - radius - 23, origin.Y - 8), Brushes.DimGray, 12, true);
        Label(dc, "E", new Point(origin.X + radius + 23, origin.Y - 8), Brushes.DimGray, 12, true);
        if (parameters == null || result == null || geometry == null)
        {
            Label(dc, "填写有效参数后显示覆盖扇形", new Point(origin.X, origin.Y + 20), Brushes.Gray, 12, true);
            return;
        }
        if (geometry.Fill.Count >= 3)
        {
            StreamGeometry fill = new StreamGeometry();
            using (StreamGeometryContext ctx = fill.Open())
            {
                ctx.BeginFigure(Project(geometry.Fill[0]), true, true);
                for (int i = 1; i < geometry.Fill.Count; i++) ctx.LineTo(Project(geometry.Fill[i]), true, false);
            }
            fill.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)Math.Round((100 - parameters.Transparency) * 255 / 100),
                0, 0, 255)), null, fill);
        }
        foreach (CoveragePath path in geometry.Lines)
        {
            Pen pen = new Pen(Brushes.Red, parameters.LineWidth);
            if (path.Dashed) pen.DashStyle = DashStyles.Dash;
            StreamGeometry line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(Project(path.Points[0]), false, false);
                for (int i = 1; i < path.Points.Count; i++) ctx.LineTo(Project(path.Points[i]), true, false);
            }
            dc.DrawGeometry(null, pen, line);
        }
        List<Tuple<Point, string>> leftLabels = new List<Tuple<Point, string>>(), rightLabels = new List<Tuple<Point, string>>();
        foreach (CoverageMarker marker in geometry.Markers)
        {
            Point position = Project(marker.Point);
            dc.DrawEllipse(Brushes.White, new Pen(Brushes.Red, 1.8), position, 3.5, 3.5);
            if (marker.Name == "站点")
                Label(dc, marker.Name, new Point(position.X - 28, position.Y - 23), Brushes.DarkRed, 11, false);
            else (position.X >= origin.X ? rightLabels : leftLabels).Add(Tuple.Create(position, marker.Name));
        }
        Callouts(dc, leftLabels, radius, false); Callouts(dc, rightLabels, radius, true);
        if (parameters.TargetDistance <= result.DisplayRadius)
        {
            Point target = Project(result.TargetPoint);
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(24, 90, 170)), 2), target, 6, 6);
        }
        Label(dc, "外圈 " + CoverageMath.DistanceText(result.DisplayRadius), new Point(14, ActualHeight - 28), Brushes.Gray, 11, false);
    }

    private void Callouts(DrawingContext dc, List<Tuple<Point, string>> labels, double radius, bool right)
    {
        labels.Sort(delegate(Tuple<Point, string> a, Tuple<Point, string> b) { return a.Item1.Y.CompareTo(b.Item1.Y); });
        double[] tops = new double[labels.Count];
        for (int i = 0; i < labels.Count; i++)
            tops[i] = Math.Max(Math.Max(22, labels[i].Item1.Y - 8), i > 0 ? tops[i - 1] + 23 : 22);
        double overflow = tops.Length == 0 ? 0 : Math.Max(0, tops[tops.Length - 1] - (ActualHeight - 48));
        double x = right ? Math.Min(ActualWidth - 155, origin.X + radius + 16) : Math.Max(8, origin.X - radius - 155);
        Pen leader = new Pen(new SolidColorBrush(Color.FromArgb(145, 220, 45, 45)), 0.8);
        for (int i = 0; i < labels.Count; i++)
        {
            double y = tops[i] - overflow;
            Point elbow = new Point(right ? x - 8 : x + 145, y + 8);
            dc.DrawLine(leader, labels[i].Item1, elbow);
            Label(dc, labels[i].Item2, new Point(x, y), Brushes.DarkRed, 11, false);
        }
    }

    private Point Project(GeoPoint point)
    {
        GeoLine line = Geodesy.Inverse(parameters.Station, point);
        double angle = line.Azimuth * CoverageMath.Radians;
        return new Point(origin.X + line.Distance * Math.Sin(angle) * scale,
            origin.Y - line.Distance * Math.Cos(angle) * scale);
    }

    private void Label(DrawingContext dc, string text, Point point, Brush brush, double size, bool center)
    {
        FormattedText formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, null,
            TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Keep annotations inside the preview even for south/east-facing sectors.
        point.X = Math.Max(5, Math.Min(ActualWidth - formatted.Width - 5, point.X - (center ? formatted.Width / 2 : 0)));
        point.Y = Math.Max(5, Math.Min(ActualHeight - formatted.Height - 5, point.Y));
        dc.DrawText(formatted, point);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (parameters == null || result == null) return;
        Focus(); dragScale = scale; dragging = CaptureMouse();
        if (dragging) UpdateTarget(e.GetPosition(this));
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (dragging) UpdateTarget(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        UpdateTarget(e.GetPosition(this)); dragging = false; ReleaseMouseCapture(); InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); dragging = false; InvalidateVisual(); }

    private void UpdateTarget(Point position)
    {
        double dx = position.X - origin.X, dy = position.Y - origin.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy) / Math.Max(1e-9, dragScale);
        if (distance < 0.1) return;
        Action<double, double> handler = TargetChanged;
        if (handler != null) handler(CoverageMath.Normalize(Math.Atan2(dx, -dy) / CoverageMath.Radians), Math.Min(100000, distance));
    }
}
