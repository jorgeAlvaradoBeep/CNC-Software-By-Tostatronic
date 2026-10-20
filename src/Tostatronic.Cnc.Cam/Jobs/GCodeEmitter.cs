using System.Globalization;
using System.Text;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>
/// Emite el G-code con el mismo formato que pcb_cnc.py (GRBL, mm, 3 decimales): encabezado
/// con comentarios, G21 G90 G17 G94, Z de traslado, M3 S + G4 P3, cortes con bajada a F de
/// plunge y avance en el primer tramo, y cierre con M5 / X0 Y0 / M2.
/// </summary>
internal static class GCodeEmitter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v) => v.ToString("0.000", Inv);

    private static void Header(StringBuilder g, CamResult r, string title, IEnumerable<string> extra, CamPreset p)
    {
        g.AppendLine($"( {r.BoardName} - {title} )");
        g.AppendLine(r.Face == BoardFace.Bottom
            ? "( mm, GRBL. X0 Y0 = esquina inf-izq vista desde el cobre. Z0 = cobre )"
            : "( mm, GRBL. X0 Y0 = esquina inf-izq de la placa, cara superior. Z0 = cobre )");
        foreach (var e in extra)
            g.AppendLine($"( {e} )");
        g.AppendLine("G21 G90 G17 G94");
        g.AppendLine($"G0 Z{N(p.TravelZMm)}");
        g.AppendLine($"M3 S{p.SpindleRpm}");
        g.AppendLine("G4 P3");
    }

    private static void Footer(StringBuilder g, CamPreset p)
    {
        g.AppendLine($"G0 Z{N(p.TravelZMm)}");
        g.AppendLine("M5");
        g.AppendLine("G0 X0 Y0");
        g.AppendLine("M2");
    }

    /// <summary>Corte de un anillo cerrado (sin punto repetido): baja, lo recorre y cierra.</summary>
    private static void CutRing(StringBuilder g, IReadOnlyList<PointD> ring, double z, int feed, CamPreset p)
    {
        g.AppendLine($"G0 Z{N(p.SafeZMm)}");
        g.AppendLine($"G0 X{N(ring[0].x)} Y{N(ring[0].y)}");
        g.AppendLine($"G1 Z{N(z)} F{p.PlungeMmMin}");
        for (int i = 1; i <= ring.Count; i++)
        {
            var pt = ring[i % ring.Count]; // el último tramo regresa al inicio
            g.Append($"G1 X{N(pt.x)} Y{N(pt.y)}");
            g.AppendLine(i == 1 ? $" F{feed}" : "");
        }
    }

    public static string Isolation(CamResult r, CamPreset p)
    {
        var g = new StringBuilder();
        Header(g, r, "Aislamiento", new[]
        {
            $"V-bit ancho efectivo {p.ToolMm.ToString("0.0##", Inv)} mm, prof {p.DepthMm.ToString("0.0##", Inv)} mm, " +
            $"{p.Passes} pasadas cada {p.StepMm.ToString("0.0##", Inv)} mm, {p.FeedMmMin} mm/min",
        }, p);
        foreach (var ring in r.Isolation)
            CutRing(g, ring, -p.DepthMm, p.FeedMmMin, p);
        Footer(g, p);
        return g.ToString();
    }

    public static string Drill(CamResult r, double bit, IReadOnlyList<PointD> pts, CamPreset p)
    {
        double zd = -(p.BoardThicknessMm + 0.2);
        var g = new StringBuilder();
        Header(g, r, $"Barrenos broca {bit.ToString("0.0#", Inv)} mm",
               new[] { $"{pts.Count} barrenos a {zd.ToString("0.0##", Inv)} mm" }, p);
        foreach (var pt in pts)
        {
            g.AppendLine($"G0 X{N(pt.x)} Y{N(pt.y)}");
            g.AppendLine("G0 Z0.500");
            g.AppendLine($"G1 Z{N(zd)} F{p.DrillFeedMmMin}");
            g.AppendLine($"G0 Z{N(p.SafeZMm)}");
        }
        Footer(g, p);
        return g.ToString();
    }

    public static string Contour(CamResult r, CamPreset p)
    {
        var g = new StringBuilder();
        Header(g, r, "Contorno", new[]
        {
            $"Fresa {p.ContourToolMm.ToString("0.0#", Inv)} mm por fuera, " +
            $"pasadas de {p.ContourStepMm.ToString("0.0##", Inv)} mm, sin pestanas",
        }, p);
        var ring = r.Contour;
        g.AppendLine($"G0 Z{N(p.SafeZMm)}");
        g.AppendLine($"G0 X{N(ring[0].x)} Y{N(ring[0].y)}");
        foreach (double z in r.ContourDepths)
        {
            g.AppendLine($"G1 Z{N(z)} F{p.PlungeMmMin}");
            for (int i = 1; i <= ring.Count; i++)
            {
                var pt = ring[i % ring.Count];
                g.AppendLine($"G1 X{N(pt.x)} Y{N(pt.y)} F{p.ContourFeedMmMin}");
            }
        }
        Footer(g, p);
        return g.ToString();
    }
}
