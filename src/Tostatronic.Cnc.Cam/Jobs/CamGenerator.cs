using System.Globalization;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Geometry;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>Cómo orientar la placa sobre la CNC.</summary>
public enum RotationMode
{
    /// <summary>Girar 90° solo si así cabe en el recorrido de la máquina y sin girar no.</summary>
    Auto,
    /// <summary>Nunca girar.</summary>
    None,
    /// <summary>Girar siempre 90°.</summary>
    Rotate90,
}

/// <summary>Opciones de generación que no son de maquinado: orientación y recorrido de la máquina.</summary>
public sealed class CamOptions
{
    public RotationMode Rotation { get; set; } = RotationMode.Auto;

    /// <summary>Recorrido útil de la máquina en X y Y (mm), para decidir el giro y avisar si no cabe.</summary>
    public double TravelXMm { get; set; } = 100;
    public double TravelYMm { get; set; } = 140;
}

/// <summary>
/// Genera las trayectorias de una cara igual que pcb_cnc.py: aislamiento = N offsets del
/// cobre unido (tool/2 + k·step) con esquinas redondas y simplificación de 0.01 mm, anillos
/// ordenados por cercanía; barrenos agrupados por broca (0.8 / 1.0 / 1.2) o broca única;
/// contorno por fuera de la placa en pasadas de profundidad. La cara inferior sale en
/// espejo X (X0 Y0 = esquina inferior izquierda VISTA DESDE EL COBRE). Si la placa no cabe
/// en el recorrido de la máquina pero sí girada, se gira 90° (como detector_cnc.py).
/// </summary>
public static class CamGenerator
{
    public static CamResult Generate(BoardModel board, BoardFace face, CamPreset p) =>
        Generate(board, face, p, new CamOptions());

    public static CamResult Generate(BoardModel board, BoardFace face, CamPreset p, CamOptions o)
    {
        var copperRaw = face == BoardFace.Bottom ? board.CopperBottom : board.CopperTop;
        if (copperRaw is not { Count: > 0 })
            throw new InvalidOperationException(
                $"La placa no trae cobre en la cara {(face == BoardFace.Bottom ? "inferior" : "superior")}.");

        bool mirror = face == BoardFace.Bottom;
        long minX = Geo.L(board.MinX), minY = Geo.L(board.MinY), maxX = Geo.L(board.MaxX);
        double w = board.WidthMm, h = board.HeightMm;

        // ---- orientación ----
        bool fitsAsIs = w <= o.TravelXMm + 1e-6 && h <= o.TravelYMm + 1e-6;
        bool fitsRotated = h <= o.TravelXMm + 1e-6 && w <= o.TravelYMm + 1e-6;
        bool rotate = o.Rotation switch
        {
            RotationMode.Rotate90 => true,
            RotationMode.None => false,
            _ => !fitsAsIs && fitsRotated,
        };
        long widthUnits = Geo.L(w);

        var copper = Geo.Union(Transform(copperRaw, mirror, rotate, minX, minY, maxX, widthUnits));
        var outline = Geo.Union(Transform(board.Outline, mirror, rotate, minX, minY, maxX, widthUnits));

        var result = new CamResult
        {
            BoardName = board.Name,
            Face = face,
            WidthMm = rotate ? h : w,
            HeightMm = rotate ? w : h,
            Rotated = rotate,
        };
        result.Warnings.AddRange(board.Warnings);
        result.Info.AddRange(board.Info);
        result.FitsMachine = result.WidthMm <= o.TravelXMm + 1e-6 && result.HeightMm <= o.TravelYMm + 1e-6;

        string size = $"{F(result.WidthMm, "0.0")} mm en X por {F(result.HeightMm, "0.0")} mm en Y";
        if (rotate)
            result.Info.Add($"Placa girada 90 grados: {size}" +
                            (o.Rotation == RotationMode.Auto ? $" (sin girar, {F(w, "0.0")} × {F(h, "0.0")} no cabe en el recorrido " +
                                                               $"{F(o.TravelXMm, "0")} × {F(o.TravelYMm, "0")})." : "."));
        else
            result.Info.Add($"Placa sin girar: {size}.");
        if (!result.FitsMachine)
            result.Warnings.Add($"La placa ({size}) NO cabe en el recorrido de la máquina ({F(o.TravelXMm, "0")} × {F(o.TravelYMm, "0")} mm)" +
                                (fitsRotated || fitsAsIs ? ", pero sí con la otra orientación." : " ni girada."));
        if (!board.FaceHasTraces(face) && board.FaceHasTraces(face == BoardFace.Bottom ? BoardFace.Top : BoardFace.Bottom))
            result.Warnings.Add($"La cara {(face == BoardFace.Bottom ? "inferior" : "superior")} solo trae pads, sin pistas: " +
                                $"las pistas están en la cara {(face == BoardFace.Bottom ? "superior" : "inferior")}. ¿Seguro que es esta la cara a fresar?");

        foreach (var path in copper)
            result.Copper.Add(ToMm(path));

        // ---- aislamiento ----
        int outersBefore = Geo.CountOuters(copper);
        for (int k = 0; k < p.Passes; k++)
        {
            var off = Geo.Simplify(Geo.Offset(copper, p.ToolMm / 2 + k * p.StepMm), 0.01);
            if (k == 0 && Geo.CountOuters(off) < outersBefore)
            {
                FindBridges(copper, p.ToolMm, result);
                string where = result.Bridges.Count > 0
                    ? " Dónde (coordenadas de la CNC): " + string.Join("; ", result.Bridges.Take(6).Select(b => $"X{F(b.x, "0.0")} Y{F(b.y, "0.0")}")) +
                      (result.Bridges.Count > 6 ? $" y {result.Bridges.Count - 6} más." : ".")
                    : "";
                result.Warnings.Add($"Hay redes más cercanas que el ancho efectivo de la herramienta ({F(p.ToolMm, "0.0#")} mm): " +
                                    "el aislamiento NO las separará; quedarán unidas. Separa los pads en el CAD o usa una punta más fina." + where);
            }
            foreach (var ring in OrderRings(off))
                result.Isolation.Add(ToMm(ring).ToList());
        }

        // ---- barrenos ----
        foreach (var group in GroupDrills(board, mirror, rotate, p))
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
        string baseName = board.Name + (face == BoardFace.Top ? "_top" : "") + (rotate ? "_girada" : "");
        result.Files.Add(($"{baseName}_1_aislamiento.nc", GCodeEmitter.Isolation(result, p)));
        foreach (var (bit, pts) in result.DrillGroups)
        {
            string bitText = bit.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', '_');
            result.Files.Add(($"{baseName}_2_barrenos_{bitText}mm.nc", GCodeEmitter.Drill(result, bit, pts, p)));
        }
        if (result.Contour.Count > 0)
            result.Files.Add(($"{baseName}_3_contorno.nc", GCodeEmitter.Contour(result, p)));
        return result;
    }

    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>
    /// Dónde se pegan dos redes: cerrar el cobre (crecer tool/2 y volver a encoger) une lo que
    /// está más cerca que tool; de la diferencia con el cobre original solo cuentan las regiones
    /// que tocan DOS polígonos distintos (las demás son los redondeos de esquinas interiores).
    /// </summary>
    private static void FindBridges(Paths64 copper, double toolMm, CamResult result)
    {
        var closed = Geo.Offset(Geo.Offset(copper, toolMm / 2), -toolMm / 2);
        var bridges = Geo.Difference(closed, copper);
        var originals = copper.Where(Clipper.IsPositive).Select(o => (Path: o, Bounds: Clipper.GetBounds(new Paths64 { o }))).ToList();
        long tol = Geo.L(0.03);
        foreach (var b in bridges)
        {
            if (!Clipper.IsPositive(b)) continue;
            if (Math.Abs(Clipper.Area(b)) < 0.0005 * Geo.Scale * Geo.Scale) continue; // < 0.0005 mm²: ruido del redondeo
            var r = Clipper.GetBounds(new Paths64 { b });
            var grown = Geo.Offset(new Paths64 { b }, 0.03);
            int touched = 0;
            foreach (var (o, ob) in originals)
            {
                if (ob.right < r.left - tol || ob.left > r.right + tol || ob.bottom < r.top - tol || ob.top > r.bottom + tol) continue;
                if (Clipper.Intersect(new Paths64 { o }, grown, FillRule.NonZero).Count > 0 && ++touched >= 2) break;
            }
            if (touched >= 2)
                result.Bridges.Add(new PointD(Geo.Mm((r.left + r.right) / 2), Geo.Mm((r.top + r.bottom) / 2)));
        }
    }

    // ---- transformación a coordenadas de trabajo ----

    /// <summary>Diseño → trabajo: traslado al origen, espejo X si es la cara inferior y, si se gira,
    /// rotación de 90° en sentido horario: (x, y) → (y, W − x), con W el ancho de la placa ya en el origen.</summary>
    private static Point64 ToWork(Point64 pt, bool mirror, bool rotate, long minX, long minY, long maxX, long widthUnits)
    {
        long x = mirror ? maxX - pt.X : pt.X - minX;
        long y = pt.Y - minY;
        return rotate ? new Point64(y, widthUnits - x) : new Point64(x, y);
    }

    private static Paths64 Transform(Paths64 paths, bool mirror, bool rotate, long minX, long minY, long maxX, long widthUnits)
    {
        var outPaths = new Paths64(paths.Count);
        foreach (var path in paths)
        {
            var np = new Path64(path.Count);
            foreach (var pt in path)
                np.Add(ToWork(pt, mirror, rotate, minX, minY, maxX, widthUnits));
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

    private static Dictionary<double, List<PointD>> GroupDrills(BoardModel board, bool mirror, bool rotate, CamPreset p)
    {
        var groups = new Dictionary<double, List<PointD>>();
        long minX = Geo.L(board.MinX), minY = Geo.L(board.MinY), maxX = Geo.L(board.MaxX), width = Geo.L(board.WidthMm);
        foreach (var hole in board.Drills)
        {
            double bit = p.DrillSingleBit ? p.DrillSingleBitMm : BitFor(hole.Diameter);
            var pt = ToWork(Geo.Pt(hole.X, hole.Y), mirror, rotate, minX, minY, maxX, width);
            if (!groups.TryGetValue(bit, out var list))
                groups[bit] = list = new List<PointD>();
            list.Add(Geo.MmPt(pt));
        }
        return groups;
    }
}
