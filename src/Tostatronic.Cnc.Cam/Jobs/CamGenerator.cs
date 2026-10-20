using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Geometry;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>
/// Genera las trayectorias de una cara igual que pcb_cnc.py: aislamiento = N offsets del
/// cobre unido (tool/2 + k·step) con esquinas redondas y simplificación de 0.01 mm, anillos
/// ordenados por cercanía; barrenos agrupados por broca (0.8 / 1.0 / 1.2) o broca única;
/// contorno por fuera de la placa en pasadas de profundidad. La cara inferior sale en
/// espejo X (X0 Y0 = esquina inferior izquierda VISTA DESDE EL COBRE).
/// </summary>
public static class CamGenerator
{
    public static CamResult Generate(BoardModel board, BoardFace face, CamPreset p)
    {
        var copperRaw = face == BoardFace.Bottom ? board.CopperBottom : board.CopperTop;
        if (copperRaw is not { Count: > 0 })
            throw new InvalidOperationException(
                $"La placa no trae cobre en la cara {(face == BoardFace.Bottom ? "inferior" : "superior")}.");

        bool mirror = face == BoardFace.Bottom;
        long minX = Geo.L(board.MinX), minY = Geo.L(board.MinY), maxX = Geo.L(board.MaxX);

        var copper = Geo.Union(Transform(copperRaw, mirror, minX, minY, maxX));
        var outline = Geo.Union(Transform(board.Outline, mirror, minX, minY, maxX));

        var result = new CamResult
        {
            BoardName = board.Name,
            Face = face,
            WidthMm = board.WidthMm,
            HeightMm = board.HeightMm,
        };
        result.Warnings.AddRange(board.Warnings);
        foreach (var path in copper)
            result.Copper.Add(ToMm(path));

        // ---- aislamiento ----
        int outersBefore = Geo.CountOuters(copper);
        for (int k = 0; k < p.Passes; k++)
        {
            var off = Geo.Simplify(Geo.Offset(copper, p.ToolMm / 2 + k * p.StepMm), 0.01);
            if (k == 0 && Geo.CountOuters(off) < outersBefore)
                result.Warnings.Add("Hay redes más cercanas que el ancho efectivo de la herramienta: " +
                                    "el aislamiento NO las separará. Revisa la vista previa.");
            foreach (var ring in OrderRings(off))
                result.Isolation.Add(ToMm(ring).ToList());
        }

        // ---- barrenos ----
        foreach (var group in GroupDrills(board, mirror, p))
            result.DrillGroups[group.Key] = OrderPoints(group.Value);

        // ---- contorno ----
        var contourPaths = Geo.Simplify(Geo.Offset(outline, p.ContourToolMm / 2), 0.01);
        if (contourPaths.Count > 0)
        {
            var ring = contourPaths.OrderByDescending(path => Math.Abs(Clipper.Area(path))).First();
            result.Contour.AddRange(ToMm(ring));
            double total = p.BoardThicknessMm + 0.2;
            int n = (int)Math.Ceiling(total / p.ContourStepMm);
            for (int i = 1; i <= n; i++)
                result.ContourDepths.Add(-Math.Min(p.ContourStepMm * i, total));
        }

        // ---- archivos ----
        string baseName = board.Name + (face == BoardFace.Top ? "_top" : "");
        result.Files.Add(($"{baseName}_1_aislamiento.nc", GCodeEmitter.Isolation(result, p)));
        foreach (var (bit, pts) in result.DrillGroups)
        {
            string bitText = bit.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
            result.Files.Add(($"{baseName}_2_barrenos_{bitText}mm.nc", GCodeEmitter.Drill(result, bit, pts, p)));
        }
        if (result.Contour.Count > 0)
            result.Files.Add(($"{baseName}_3_contorno.nc", GCodeEmitter.Contour(result, p)));
        return result;
    }

    // ---- transformación a coordenadas de trabajo ----

    private static Paths64 Transform(Paths64 paths, bool mirror, long minX, long minY, long maxX)
    {
        var outPaths = new Paths64(paths.Count);
        foreach (var path in paths)
        {
            var np = new Path64(path.Count);
            foreach (var pt in path)
                np.Add(mirror
                    ? new Point64(maxX - pt.X, pt.Y - minY)
                    : new Point64(pt.X - minX, pt.Y - minY));
            outPaths.Add(np);
        }
        return outPaths;
    }

    private static PathD ToMm(Path64 path)
    {
        var d = new PathD(path.Count);
        foreach (var pt in path)
            d.Add(Geo.MmPt(pt));
        return d;
    }

    // ---- orden por cercanía (como order() de pcb_cnc.py, pero además cada anillo
    //      arranca en su vértice más cercano a la posición actual) ----

    private static List<Path64> OrderRings(Paths64 rings)
    {
        var remaining = rings.Where(r => r.Count >= 3).ToList();
        var ordered = new List<Path64>(remaining.Count);
        var cur = new Point64(0, 0);
        while (remaining.Count > 0)
        {
            int bestRing = 0, bestVertex = 0;
            double best = double.MaxValue;
            for (int r = 0; r < remaining.Count; r++)
            {
                var ring = remaining[r];
                for (int v = 0; v < ring.Count; v++)
                {
                    double dx = ring[v].X - cur.X, dy = ring[v].Y - cur.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; bestRing = r; bestVertex = v; }
                }
            }
            var chosen = remaining[bestRing];
            remaining.RemoveAt(bestRing);
            var rotated = new Path64(chosen.Count);
            for (int v = 0; v < chosen.Count; v++)
                rotated.Add(chosen[(bestVertex + v) % chosen.Count]);
            ordered.Add(rotated);
            cur = rotated[0]; // el anillo termina donde empieza
        }
        return ordered;
    }

    private static List<PointD> OrderPoints(List<PointD> pts)
    {
        var remaining = pts.ToList();
        var ordered = new List<PointD>(pts.Count);
        var cur = new PointD(0, 0);
        while (remaining.Count > 0)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                double dx = remaining[i].x - cur.x, dy = remaining[i].y - cur.y;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD) { bestD = d2; best = i; }
            }
            cur = remaining[best];
            ordered.Add(cur);
            remaining.RemoveAt(best);
        }
        return ordered;
    }

    // ---- barrenos ----

    /// <summary>Broca para un diámetro de diseño, como pcb_cnc.py: &lt;1.0 → 0.8, &lt;1.2 → 1.0, resto → 1.2.</summary>
    public static double BitFor(double designDiameter) =>
        designDiameter < 1.0 ? 0.8 : designDiameter < 1.2 ? 1.0 : 1.2;

    private static Dictionary<double, List<PointD>> GroupDrills(BoardModel board, bool mirror, CamPreset p)
    {
        var groups = new Dictionary<double, List<PointD>>();
        foreach (var hole in board.Drills)
        {
            double bit = p.DrillSingleBit ? p.DrillSingleBitMm : BitFor(hole.Diameter);
            double x = mirror ? board.MaxX - hole.X : hole.X - board.MinX;
            double y = hole.Y - board.MinY;
            if (!groups.TryGetValue(bit, out var list))
                groups[bit] = list = new List<PointD>();
            list.Add(new PointD(x, y));
        }
        return groups;
    }
}
