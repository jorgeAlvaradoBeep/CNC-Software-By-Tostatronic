using Clipper2Lib;

namespace Tostatronic.Cnc.Cam.Geometry;

/// <summary>
/// Utilería de geometría sobre Clipper2. Se trabaja en enteros de 0.1 µm (Scale) y se
/// convierte a mm solo en los bordes. Los redondeos usan la misma suavidad que los
/// buffers de shapely con quad_segs=16 (los scripts de referencia de Jorge).
/// </summary>
public static class Geo
{
    public const double Scale = 10000.0;          // 1 unidad = 0.1 µm
    public const double ArcTolUnits = 0.002 * Scale;
    public const int CircleSegments = 64;         // como quad_segs=16 de shapely

    public static long L(double mm) => (long)Math.Round(mm * Scale);
    public static double Mm(long units) => units / Scale;
    public static Point64 Pt(double xMm, double yMm) => new(L(xMm), L(yMm));
    public static PointD MmPt(Point64 p) => new(p.X / Scale, p.Y / Scale);

    public static Path64 Circle(double cx, double cy, double r, int segs = CircleSegments)
    {
        var path = new Path64(segs);
        for (int i = 0; i < segs; i++)
        {
            double a = 2 * Math.PI * i / segs;
            path.Add(Pt(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return path;
    }

    public static Path64 Rect(double cx, double cy, double w, double h, double rotDeg = 0)
    {
        var pts = new[]
        {
            (-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2),
        };
        var path = new Path64(4);
        foreach (var (x, y) in pts)
        {
            var (rx, ry) = Rotate(x, y, rotDeg);
            path.Add(Pt(cx + rx, cy + ry));
        }
        return path;
    }

    /// <summary>Óvalo tipo pad "O": rectángulo con extremos redondos (segmento bufferizado).</summary>
    public static Path64 Obround(double cx, double cy, double w, double h, double rotDeg = 0)
    {
        if (Math.Abs(w - h) < 1e-9)
            return Circle(cx, cy, w / 2);
        bool horizontal = w > h;
        double half = (Math.Abs(w - h)) / 2;
        double radius = Math.Min(w, h) / 2;
        var a = horizontal ? (-half, 0.0) : (0.0, -half);
        var b = horizontal ? (half, 0.0) : (0.0, half);
        var (ax, ay) = Rotate(a.Item1, a.Item2, rotDeg);
        var (bx, by) = Rotate(b.Item1, b.Item2, rotDeg);
        var seg = new Paths64 { new Path64 { Pt(cx + ax, cy + ay), Pt(cx + bx, cy + by) } };
        var inflated = Clipper.InflatePaths(seg, radius * Scale, JoinType.Round, EndType.Round, 2.0, ArcTolUnits);
        return inflated.Count > 0 ? inflated[0] : Circle(cx, cy, radius);
    }

    public static (double X, double Y) Rotate(double x, double y, double deg)
    {
        if (deg == 0) return (x, y);
        double rad = deg * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        return (x * c - y * s, x * s + y * c);
    }

    public static Paths64 Union(Paths64 paths) => Clipper.Union(paths, FillRule.NonZero);

    public static Paths64 Union(Paths64 a, Paths64 b) => Clipper.Union(a, b, FillRule.NonZero);

    public static Paths64 Difference(Paths64 a, Paths64 b) => Clipper.Difference(a, b, FillRule.NonZero);

    /// <summary>Offset de polígonos cerrados (positivo = crecer), con esquinas redondas.</summary>
    public static Paths64 Offset(Paths64 polys, double deltaMm) =>
        Clipper.InflatePaths(polys, deltaMm * Scale, JoinType.Round, EndType.Polygon, 2.0, ArcTolUnits);

    /// <summary>Trazo de un segmento con grosor (extremos redondos), como un buffer de shapely.</summary>
    public static Paths64 StrokeSegment(Point64 a, Point64 b, double widthMm)
    {
        if (a == b)
            return new Paths64 { Circle(Mm(a.X), Mm(a.Y), widthMm / 2) };
        var open = new Paths64 { new Path64 { a, b } };
        return Clipper.InflatePaths(open, widthMm / 2 * Scale, JoinType.Round, EndType.Round, 2.0, ArcTolUnits);
    }

    public static Paths64 Simplify(Paths64 paths, double epsilonMm) =>
        Clipper.SimplifyPaths(paths, epsilonMm * Scale, false);

    /// <summary>¿El punto cae dentro del relleno (regla NonZero con orientaciones de Clipper)?</summary>
    public static bool InsideFilled(Point64 pt, Paths64 filled)
    {
        int winding = 0;
        foreach (var path in filled)
        {
            if (Clipper.PointInPolygon(pt, path) != PointInPolygonResult.IsOutside)
                winding += Clipper.IsPositive(path) ? 1 : -1;
        }
        return winding > 0;
    }

    public static int CountOuters(Paths64 filled)
    {
        int n = 0;
        foreach (var p in filled)
            if (Clipper.IsPositive(p)) n++;
        return n;
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) BoundsMm(Paths64 paths)
    {
        var r = Clipper.GetBounds(paths);
        return (Mm(r.left), Mm(r.top), Mm(r.right), Mm(r.bottom));
    }
}
