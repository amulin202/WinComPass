using System;
using System.Collections.Generic;
using System.Globalization;

public enum CoverageMode { Engineering, TargetDistance }
public enum TargetBinding { Far, Center }

public sealed class CoverageParameters
{
    public double Latitude = 39.9042;
    public double Longitude = 116.4074;
    public double Azimuth = 120;
    public double HorizontalBeamwidth = 65;
    public double VerticalBeamwidth = 8;
    public double AntennaHeight = 30;
    public double ReceiverHeight;
    public double SiteElevation;
    public double TargetElevation;
    public double MechanicalTilt = 2;
    public double ElectricalTilt = 4;
    public double TargetDistance = 859.0875984874681;
    public double DisplayLimit = 5000;
    public double Transparency = 70;
    public double LineWidth = 2;
    public bool GroundBandOnly;
    public CoverageMode Mode;
    public TargetBinding Binding;

    public CoverageParameters Copy() { return (CoverageParameters)MemberwiseClone(); }
    public GeoPoint Station { get { return new GeoPoint(Latitude, Longitude); } }
}

public struct GeoPoint
{
    public readonly double Latitude, Longitude;
    public GeoPoint(double latitude, double longitude)
    {
        if (!CoverageMath.Finite(latitude) || Math.Abs(latitude) > 90 ||
            !CoverageMath.Finite(longitude) || Math.Abs(longitude) > 180)
            throw new ArgumentException("经纬度超出范围：纬度 -90～90，经度 -180～180。");
        Latitude = latitude;
        Longitude = longitude;
    }
    public override string ToString()
    {
        return Latitude.ToString("F7", CultureInfo.InvariantCulture) + ", " +
            Longitude.ToString("F7", CultureInfo.InvariantCulture);
    }
}

public struct GeoLine
{
    public readonly double Distance, Azimuth;
    public GeoLine(double distance, double azimuth) { Distance = distance; Azimuth = azimuth; }
}

public sealed class CoverageResult
{
    public double Height, Tilt, CurrentTilt, DisplayRadius;
    public double? Near, Center, Far;
    public string Notice;
    public GeoPoint NearPoint, CenterPoint, FarPoint, TargetPoint;
    public bool HasNearPoint, HasCenterPoint, HasFarPoint;
    public bool Truncated;
}

public sealed class CoveragePath
{
    public string Name;
    public readonly List<GeoPoint> Points = new List<GeoPoint>();
    public bool Dashed;
}

public sealed class CoverageMarker
{
    public string Name;
    public GeoPoint Point;
}

public sealed class CoverageGeometry
{
    public readonly List<GeoPoint> Fill = new List<GeoPoint>();
    public readonly List<CoveragePath> Lines = new List<CoveragePath>();
    public readonly List<CoverageMarker> Markers = new List<CoverageMarker>();
}

public static class CoverageMath
{
    public const double Radians = Math.PI / 180;
    public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    public static double Normalize(double angle) { return (angle % 360 + 360) % 360; }

    private static void Range(double value, double min, double max, string name)
    {
        if (!Finite(value) || value < min || value > max)
            throw new ArgumentException(name + "应在 " + min + "～" + max + " 之间。");
    }

    public static CoverageResult Calculate(CoverageParameters p)
    {
        if (p == null) throw new ArgumentNullException("p");
        GeoPoint station = p.Station;
        Range(p.Azimuth, -360000, 360000, "方位角");
        Range(p.HorizontalBeamwidth, 0.1, 360, "水平波束宽度");
        Range(p.VerticalBeamwidth, 0.1, 179, "垂直波束宽度");
        Range(p.AntennaHeight, 0, 10000, "天线挂高");
        Range(p.ReceiverHeight, 0, 10000, "接收点高度");
        Range(p.SiteElevation, -1000, 10000, "站址地面海拔");
        Range(p.TargetElevation, -1000, 10000, "目标地面海拔");
        Range(p.MechanicalTilt, -90, 90, "机械下倾角");
        Range(p.ElectricalTilt, -90, 90, "电下倾角");
        Range(p.TargetDistance, 0.1, 100000, "目标距离（米）");
        Range(p.DisplayLimit, 1, 100000, "最大显示距离（米）");
        Range(p.Transparency, 0, 100, "透明度（%）");
        Range(p.LineWidth, 1, 10, "线宽");
        if (!Enum.IsDefined(typeof(CoverageMode), p.Mode) || !Enum.IsDefined(typeof(TargetBinding), p.Binding))
            throw new ArgumentException("计算模式或终点绑定无效。");
        CoverageResult r = new CoverageResult();
        r.Height = p.SiteElevation + p.AntennaHeight - p.TargetElevation - p.ReceiverHeight;
        if (r.Height <= 0) throw new ArgumentException("天线中心必须高于目标接收平面，请检查挂高、海拔及接收点高度。");
        r.CurrentTilt = p.MechanicalTilt + p.ElectricalTilt;
        r.Tilt = p.Mode == CoverageMode.Engineering ? r.CurrentTilt :
            Math.Atan2(r.Height, p.TargetDistance) / Radians +
            (p.Binding == TargetBinding.Far ? p.VerticalBeamwidth / 2 : 0);
        double nearAngle = r.Tilt + p.VerticalBeamwidth / 2;
        if (nearAngle > 90 + 1e-10)
            throw new ArgumentException("主瓣下边缘超过垂直向下方向，请减小下倾角、波束宽度或增大目标距离。");
        if (nearAngle <= 0)
            throw new ArgumentException("主瓣在当前平坦地面模型下没有前方落点，请调整高度或下倾角。");
        r.Near = Landing(r.Height, nearAngle);
        r.Center = Landing(r.Height, r.Tilt);
        r.Far = Landing(r.Height, r.Tilt - p.VerticalBeamwidth / 2);
        r.DisplayRadius = r.Far.HasValue ? Math.Min(p.DisplayLimit, r.Far.Value) : p.DisplayLimit;
        r.Truncated = !r.Far.HasValue || r.Far.Value > p.DisplayLimit;
        List<string> notices = new List<string>();
        if (!r.Center.HasValue) notices.Add("主轴无有限地面落点");
        if (!r.Far.HasValue) notices.Add("远边界无有限地面落点");
        if (r.Truncated) notices.Add("绘图截断于 " + DistanceText(r.DisplayRadius));
        if (r.Near.Value > r.DisplayRadius) notices.Add("近点超出显示范围");
        notices.Add("平坦地面波瓣几何投影");
        r.Notice = string.Join("；", notices.ToArray());
        r.HasNearPoint = InDisplay(r.Near, r.DisplayRadius);
        r.HasCenterPoint = InDisplay(r.Center, r.DisplayRadius);
        r.HasFarPoint = InDisplay(r.Far, r.DisplayRadius);
        if (r.HasNearPoint) r.NearPoint = Geodesy.Direct(station, p.Azimuth, r.Near.Value);
        if (r.HasCenterPoint) r.CenterPoint = Geodesy.Direct(station, p.Azimuth, r.Center.Value);
        if (r.HasFarPoint) r.FarPoint = Geodesy.Direct(station, p.Azimuth, r.Far.Value);
        r.TargetPoint = Geodesy.Direct(station, p.Azimuth, p.TargetDistance);
        return r;
    }

    private static double? Landing(double height, double angle)
    {
        // Angles at/beyond the horizon have no finite forward intersection.
        if (angle <= 0 || angle > 90 + 1e-10) return null;
        return Math.Abs(angle - 90) < 1e-10 ? 0 : height / Math.Tan(angle * Radians);
    }

    private static bool InDisplay(double? distance, double radius)
    { return distance.HasValue && distance.Value <= radius + 1e-7; }

    public static string DistanceText(double? value)
    { return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + " m" : "无有限落点"; }

    public static CoverageGeometry BuildGeometry(CoverageParameters p, CoverageResult r)
    {
        CoverageGeometry g = new CoverageGeometry();
        GeoPoint origin = p.Station;
        List<GeoPoint> outer = Arc(p, r.DisplayRadius);
        if (!p.GroundBandOnly) { g.Fill.Add(origin); g.Fill.AddRange(outer); }
        else if (r.Near.Value < r.DisplayRadius)
        {
            g.Fill.AddRange(outer);
            List<GeoPoint> inner = Arc(p, r.Near.Value);
            inner.Reverse();
            g.Fill.AddRange(inner);
        }
        CoveragePath sides = new CoveragePath { Name = "扇形侧边界" };
        sides.Points.Add(outer[0]); sides.Points.Add(origin); sides.Points.Add(outer[outer.Count - 1]);
        g.Lines.Add(sides);
        CoveragePath axis = new CoveragePath { Name = "中心方向", Dashed = true };
        axis.Points.Add(origin); axis.Points.Add(Geodesy.Direct(origin, p.Azimuth, r.DisplayRadius));
        g.Lines.Add(axis);
        AddArc(g, p, r.Near, r.DisplayRadius, "近点", r.NearPoint, r.HasNearPoint);
        AddArc(g, p, r.Center, r.DisplayRadius, "中点", r.CenterPoint, r.HasCenterPoint);
        AddArc(g, p, r.Far, r.DisplayRadius, "远点", r.FarPoint, r.HasFarPoint);
        if (r.Truncated)
        {
            CoveragePath clip = new CoveragePath { Name = "显示截断 " + DistanceText(r.DisplayRadius), Dashed = true };
            clip.Points.AddRange(outer); g.Lines.Add(clip);
        }
        g.Markers.Insert(0, new CoverageMarker { Name = "站点", Point = origin });
        bool coincides = (r.Near.HasValue && Math.Abs(p.TargetDistance - r.Near.Value) < 0.01) ||
            (r.Center.HasValue && Math.Abs(p.TargetDistance - r.Center.Value) < 0.01) ||
            (r.Far.HasValue && Math.Abs(p.TargetDistance - r.Far.Value) < 0.01);
        if (p.TargetDistance <= r.DisplayRadius && p.Mode == CoverageMode.Engineering && !coincides)
            g.Markers.Add(new CoverageMarker { Name = "目标参考 " + DistanceText(p.TargetDistance), Point = r.TargetPoint });
        return g;
    }

    private static void AddArc(CoverageGeometry g, CoverageParameters p, double? distance,
        double radius, string name, GeoPoint point, bool visible)
    {
        if (!visible) return;
        CoveragePath path = new CoveragePath { Name = name + " " + DistanceText(distance) };
        path.Points.AddRange(Arc(p, distance.Value)); g.Lines.Add(path);
        g.Markers.Add(new CoverageMarker { Name = path.Name, Point = point });
    }

    private static List<GeoPoint> Arc(CoverageParameters p, double radius)
    {
        // Cap chord sagitta at about 0.5 m and angular spacing at one degree.
        double step = radius > 0.5 ? Math.Min(1, 2 * Math.Acos(1 - 0.5 / radius) / Radians) : 1;
        int count = Math.Max(2, (int)Math.Ceiling(p.HorizontalBeamwidth / step));
        List<GeoPoint> points = new List<GeoPoint>(count + 1);
        for (int i = 0; i <= count; i++)
            points.Add(Geodesy.Direct(p.Station, p.Azimuth - p.HorizontalBeamwidth / 2 + p.HorizontalBeamwidth * i / count, radius));
        return points;
    }
}

// Vincenty's direct/inverse algorithms on the WGS84 ellipsoid. Non-convergence
// is reported rather than substituting an unlabelled spherical distance.
public static class Geodesy
{
    private const double A = 6378137.0, F = 1 / 298.257223563, B = A * (1 - F);
    private const double Rad = Math.PI / 180;

    public static GeoPoint Direct(GeoPoint start, double azimuth, double distance)
    {
        if (!CoverageMath.Finite(distance) || distance < 0 || !CoverageMath.Finite(azimuth))
            throw new ArgumentException("距离和方位角必须为有限数，距离不得为负。");
        if (distance == 0) return start;
        double alpha = CoverageMath.Normalize(azimuth) * Rad;
        double sinAlpha1 = Math.Sin(alpha), cosAlpha1 = Math.Cos(alpha);
        double tanU1 = (1 - F) * Math.Tan(start.Latitude * Rad);
        double cosU1 = 1 / Math.Sqrt(1 + tanU1 * tanU1), sinU1 = tanU1 * cosU1;
        double sigma1 = Math.Atan2(tanU1, cosAlpha1);
        double sinAlpha = cosU1 * sinAlpha1, cosSqAlpha = 1 - sinAlpha * sinAlpha;
        double uSq = cosSqAlpha * (A * A - B * B) / (B * B);
        double bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        double bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        double sigma = distance / (B * bigA), previous;
        int iteration = 0;
        do
        {
            double cos2 = Math.Cos(2 * sigma1 + sigma), sinS = Math.Sin(sigma), cosS = Math.Cos(sigma);
            double delta = bigB * sinS * (cos2 + bigB / 4 * (cosS * (-1 + 2 * cos2 * cos2) -
                bigB / 6 * cos2 * (-3 + 4 * sinS * sinS) * (-3 + 4 * cos2 * cos2)));
            previous = sigma; sigma = distance / (B * bigA) + delta;
        } while (Math.Abs(sigma - previous) > 1e-12 && ++iteration < 200);
        if (iteration >= 200) throw new ArgumentException("测地线正算未收敛。");
        double sinSigma = Math.Sin(sigma), cosSigma = Math.Cos(sigma);
        double cos2Sigma = Math.Cos(2 * sigma1 + sigma);
        double tmp = sinU1 * sinSigma - cosU1 * cosSigma * cosAlpha1;
        double latitude = Math.Atan2(sinU1 * cosSigma + cosU1 * sinSigma * cosAlpha1,
            (1 - F) * Math.Sqrt(sinAlpha * sinAlpha + tmp * tmp));
        double lambda = Math.Atan2(sinSigma * sinAlpha1, cosU1 * cosSigma - sinU1 * sinSigma * cosAlpha1);
        double c = F / 16 * cosSqAlpha * (4 + F * (4 - 3 * cosSqAlpha));
        double l = lambda - (1 - c) * F * sinAlpha * (sigma + c * sinSigma *
            (cos2Sigma + c * cosSigma * (-1 + 2 * cos2Sigma * cos2Sigma)));
        double longitude = (start.Longitude + l / Rad + 540) % 360 - 180;
        return new GeoPoint(latitude / Rad, longitude);
    }

    public static GeoLine Inverse(GeoPoint start, GeoPoint end)
    {
        double u1 = Math.Atan((1 - F) * Math.Tan(start.Latitude * Rad));
        double u2 = Math.Atan((1 - F) * Math.Tan(end.Latitude * Rad));
        double su1 = Math.Sin(u1), cu1 = Math.Cos(u1), su2 = Math.Sin(u2), cu2 = Math.Cos(u2);
        double l = ((end.Longitude - start.Longitude + 540) % 360 - 180) * Rad, lambda = l;
        double sinSigma = 0, cosSigma = 0, sigma = 0, sinAlpha = 0, cosSqAlpha = 0, cos2Sigma = 0;
        bool converged = false;
        for (int i = 0; i < 200; i++)
        {
            double sl = Math.Sin(lambda), cl = Math.Cos(lambda);
            double x = cu2 * sl, y = cu1 * su2 - su1 * cu2 * cl;
            sinSigma = Math.Sqrt(x * x + y * y);
            cosSigma = su1 * su2 + cu1 * cu2 * cl;
            if (sinSigma < 1e-15)
            {
                if (cosSigma > 0) return new GeoLine(0, 0);
                throw new ArgumentException("两点接近对跖点，超出工程拉线范围。");
            }
            sigma = Math.Atan2(sinSigma, cosSigma);
            sinAlpha = cu1 * cu2 * sl / sinSigma; cosSqAlpha = Math.Max(0, 1 - sinAlpha * sinAlpha);
            cos2Sigma = cosSqAlpha > 1e-15 ? cosSigma - 2 * su1 * su2 / cosSqAlpha : 0;
            double c = F / 16 * cosSqAlpha * (4 + F * (4 - 3 * cosSqAlpha));
            double previous = lambda;
            lambda = l + (1 - c) * F * sinAlpha * (sigma + c * sinSigma *
                (cos2Sigma + c * cosSigma * (-1 + 2 * cos2Sigma * cos2Sigma)));
            if (Math.Abs(lambda - previous) < 1e-12) { converged = true; break; }
        }
        if (!converged) throw new ArgumentException("两点测地线反算未收敛，请选取更近的目标点。");
        double uSq = cosSqAlpha * (A * A - B * B) / (B * B);
        double bigA = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        double bigB = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        double delta = bigB * sinSigma * (cos2Sigma + bigB / 4 * (cosSigma * (-1 + 2 * cos2Sigma * cos2Sigma) -
            bigB / 6 * cos2Sigma * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2Sigma * cos2Sigma)));
        double bearing = Math.Atan2(cu2 * Math.Sin(lambda), cu1 * su2 - su1 * cu2 * Math.Cos(lambda)) / Rad;
        return new GeoLine(B * bigA * (sigma - delta), CoverageMath.Normalize(bearing));
    }
}
