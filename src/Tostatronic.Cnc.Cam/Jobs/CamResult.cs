using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>Trayectorias generadas para una cara, en coordenadas de trabajo (mm): X0 Y0 =
/// esquina inferior izquierda de la placa tal como se ve sobre la CNC, Z0 = cobre.</summary>
public sealed class CamResult
{
    public required string BoardName { get; init; }
    public required BoardFace Face { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }

    /// <summary>Cobre ya transformado (para la vista previa).</summary>
    public PathsD Copper { get; } = new();

    /// <summary>Anillos de aislamiento en orden de corte (cada anillo cerrado, sin repetir el punto inicial).</summary>
    public List<List<PointD>> Isolation { get; } = new();

    /// <summary>Barrenos por broca, en orden de taladrado.</summary>
    public SortedDictionary<double, List<PointD>> DrillGroups { get; } = new();

    /// <summary>Anillo del contorno de corte (cerrado, sin repetir el inicial); vacío si no hay contorno.</summary>
    public List<PointD> Contour { get; } = new();
    public List<double> ContourDepths { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>Archivos .nc generados, en orden de uso.</summary>
    public List<(string FileName, string Content)> Files { get; } = new();

    public int DrillCount => DrillGroups.Values.Sum(g => g.Count);
}
