using Clipper2Lib;
using Tostatronic.Cnc.Cam.Geometry;

namespace Tostatronic.Cnc.Cam.Board;

/// <summary>
/// Convierte los trazos sueltos de la capa de contorno (segmentos finos, arcos ya teselados)
/// en polígonos cerrados, encadenando extremos que coinciden dentro de una tolerancia.
/// </summary>
public static class OutlineChainer
{
    private const double TolMm = 0.02;

    public static Paths64 Chain(IReadOnlyList<(PointD A, PointD B)> segments, List<string>? warnings = null)
    {
        var result = new Paths64();
        if (segments.Count == 0) return result;

        var used = new bool[segments.Count];
        // índice de extremos por celda de rejilla de tolerancia
        var index = new Dictionary<(long, long), List<(int Seg, bool FromA)>>();
        void Add((long, long) key, int seg, bool fromA)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            list.Add((seg, fromA));
        }
        static (long, long) Key(PointD p) =>
            ((long)Math.Round(p.x / TolMm), (long)Math.Round(p.y / TolMm));
        for (int s = 0; s < segments.Count; s++)
        {
            Add(Key(segments[s].A), s, true);
            Add(Key(segments[s].B), s, false);
        }

        (int Seg, bool FromA)? FindNext(PointD at, int exceptSeg)
        {
            var (kx, ky) = Key(at);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!index.TryGetValue((kx + dx, ky + dy), out var list)) continue;
                    foreach (var (seg, fromA) in list)
                    {
                        if (used[seg] || seg == exceptSeg) continue;
                        var start = fromA ? segments[seg].A : segments[seg].B;
                        if (Math.Abs(start.x - at.x) <= TolMm && Math.Abs(start.y - at.y) <= TolMm)
                            return (seg, fromA);
                    }
                }
            return null;
        }

        for (int s = 0; s < segments.Count; s++)
        {
            if (used[s]) continue;
            used[s] = true;
            var pts = new List<PointD> { segments[s].A, segments[s].B };
            var startPt = segments[s].A;
            int guard = segments.Count + 2;
            while (guard-- > 0)
            {
                var cur = pts[^1];
                if (pts.Count > 2 && Math.Abs(cur.x - startPt.x) <= TolMm && Math.Abs(cur.y - startPt.y) <= TolMm)
                    break; // cerrado
                var next = FindNext(cur, -1);
                if (next == null) break;
                used[next.Value.Seg] = true;
                var (a, b) = segments[next.Value.Seg];
                pts.Add(next.Value.FromA ? b : a);
            }

            bool closed = pts.Count > 3 &&
                          Math.Abs(pts[^1].x - startPt.x) <= TolMm && Math.Abs(pts[^1].y - startPt.y) <= TolMm;
            if (!closed)
            {
                warnings?.Add("El contorno de la placa tiene tramos abiertos; se usará el rectángulo envolvente.");
                continue;
            }
            var path = new Path64(pts.Count - 1);
            for (int k = 0; k < pts.Count - 1; k++)
                path.Add(Geo.Pt(pts[k].x, pts[k].y));
            if (!Clipper.IsPositive(path)) path.Reverse();
            if (Math.Abs(Clipper.Area(path)) > Geo.Scale * Geo.Scale * 0.01) // > 0.01 mm²
                result.Add(path);
        }
        return result;
    }
}
