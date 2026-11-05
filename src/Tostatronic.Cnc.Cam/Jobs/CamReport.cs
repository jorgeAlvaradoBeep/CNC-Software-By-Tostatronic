using System.Globalization;
using System.Text;
using Tostatronic.Cnc.Cam.Board;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>Reporte de validación en texto: lo que se ve en pantalla, para dejarlo junto a los .nc.</summary>
public static class CamReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string FileNameFor(CamResult r) => r.BoardName + "_reporte.txt";

    public static string Build(CamResult r, CamPreset p, CamOptions o, string? source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.BoardName} - reporte de validación ({DateTime.Now:yyyy-MM-dd HH:mm})");
        if (source != null) sb.AppendLine($"Fuente: {source}");
        sb.AppendLine($"Cara: {(r.Face == BoardFace.Bottom ? "inferior (espejo, vista desde el cobre)" : "superior (sin espejo)")}");
        sb.AppendLine($"Tamaño sobre la CNC: {r.WidthMm.ToString("0.00", Inv)} × {r.HeightMm.ToString("0.00", Inv)} mm" +
                      $"{(r.Rotated ? " (girada 90°)" : "")}; recorrido de la máquina {o.TravelXMm.ToString("0", Inv)} × " +
                      $"{o.TravelYMm.ToString("0", Inv)} mm: {(r.FitsMachine ? "cabe" : "NO CABE")}");
        sb.AppendLine($"Preset: {p.Name} (ancho efectivo {p.ToolMm.ToString("0.0##", Inv)} mm, prof {p.DepthMm.ToString("0.0##", Inv)} mm, " +
                      $"{p.Passes} pasadas cada {p.StepMm.ToString("0.0##", Inv)} mm, F{p.FeedMmMin}/{p.PlungeMmMin}, {p.SpindleRpm} rpm)");
        sb.AppendLine($"Aislamiento: {r.Isolation.Count} anillos, {r.IsolationLengthMm.ToString("0", Inv)} mm de corte " +
                      $"(~{Math.Ceiling(r.IsolationLengthMm / Math.Max(1, p.FeedMmMin))} min)");
        sb.AppendLine($"Barrenos: {r.DrillCount}; " + string.Join(", ", r.DrillGroups.Select(g => $"broca {g.Key.ToString("0.0#", Inv)} mm: {g.Value.Count}")) +
                      $"; a {(-(p.BoardThicknessMm + 0.2)).ToString("0.0#", Inv)} mm");
        sb.AppendLine($"Contorno: {(r.Contour.Count > 0 ? $"{r.ContourDepths.Count} pasadas de {p.ContourStepMm.ToString("0.0#", Inv)} mm con fresa de {p.ContourToolMm.ToString("0.0#", Inv)}" : "no")}");
        sb.AppendLine();
        sb.AppendLine("Hallazgos:");
        foreach (var i in r.Info) sb.AppendLine("  - " + i);
        sb.AppendLine();
        sb.AppendLine(r.Warnings.Count == 0 ? "Sin avisos." : "AVISOS (revisar antes de fresar):");
        foreach (var w in r.Warnings) sb.AppendLine("  ! " + w);
        sb.AppendLine();
        sb.AppendLine("Archivos, en orden de uso (mismo origen X0 Y0; entre uno y otro solo se cambia la herramienta y se vuelve a tocar Z0):");
        foreach (var (name, _) in r.Files) sb.AppendLine("  " + name);
        return sb.ToString();
    }
}
