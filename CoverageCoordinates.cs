using System;

// GCJ-02 conversion adapted from googollee/eviltransform (BSD-2-Clause).
// Copyright (c) 2015 Googol Lee and contributors. See ThirdPartyNotices.txt.
public static class CoverageCoordinates
{
    private const double Axis = 6378137.0, Eccentricity = 0.00669342162296594323;

    private static bool Outside(GeoPoint p)
    { return p.Longitude < 72.004 || p.Longitude > 137.8347 || p.Latitude < 0.8293 || p.Latitude > 55.8271; }

    public static GeoPoint ToGcj02(GeoPoint wgs)
    {
        if (Outside(wgs)) return wgs;
        double x = wgs.Longitude - 105, y = wgs.Latitude - 35;
        double latitude = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        latitude += (20 * Math.Sin(6 * x * Math.PI) + 20 * Math.Sin(2 * x * Math.PI)) * 2 / 3;
        latitude += (20 * Math.Sin(y * Math.PI) + 40 * Math.Sin(y / 3 * Math.PI)) * 2 / 3;
        latitude += (160 * Math.Sin(y / 12 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30)) * 2 / 3;
        double longitude = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        longitude += (20 * Math.Sin(6 * x * Math.PI) + 20 * Math.Sin(2 * x * Math.PI)) * 2 / 3;
        longitude += (20 * Math.Sin(x * Math.PI) + 40 * Math.Sin(x / 3 * Math.PI)) * 2 / 3;
        longitude += (150 * Math.Sin(x / 12 * Math.PI) + 300 * Math.Sin(x / 30 * Math.PI)) * 2 / 3;
        double radians = wgs.Latitude * CoverageMath.Radians;
        double magic = Math.Sin(radians); magic = 1 - Eccentricity * magic * magic;
        double root = Math.Sqrt(magic);
        latitude = latitude * 180 / ((Axis * (1 - Eccentricity)) / (magic * root) * Math.PI);
        longitude = longitude * 180 / (Axis / root * Math.Cos(radians) * Math.PI);
        return new GeoPoint(wgs.Latitude + latitude, wgs.Longitude + longitude);
    }

    public static GeoPoint FromGcj02(GeoPoint gcj)
    {
        if (Outside(gcj)) return gcj;
        double latitude = gcj.Latitude, longitude = gcj.Longitude;
        for (int i = 0; i < 30; i++)
        {
            GeoPoint projected = ToGcj02(new GeoPoint(latitude, longitude));
            double latError = projected.Latitude - gcj.Latitude, lngError = projected.Longitude - gcj.Longitude;
            if (Math.Abs(latError) < 1e-9 && Math.Abs(lngError) < 1e-9)
                return new GeoPoint(latitude, longitude);
            latitude -= latError; longitude -= lngError;
        }
        throw new ArgumentException("GCJ-02 转换未收敛，请改用 WGS84 / CGCS2000 坐标。");
    }

    public static GeoPoint FromOvit(double latitude, double longitude, int coordinateType)
    {
        GeoPoint point = new GeoPoint(latitude, longitude);
        if (coordinateType == 0) return point;
        if (coordinateType == 1) return FromGcj02(point);
        throw new ArgumentException("奥维返回了当前工程坐标，请先在奥维中切换为经纬度坐标后取点。");
    }
}
