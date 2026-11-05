using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>Trayectorias generadas para una cara, en coordenadas de trabajo (mm): X0 Y0 =
/// esquina inferior izquierda de la placa tal como se ve sobre la CNC, Z0 = cobre.</summary>
public sealed class CamResult
{
    public required string BoardName { get; init; }
    public required BoardFace Face { get; init; }

    /// <summary>Tamaño de la placa como queda sobre la CNC (ya girada si Rotated).</summary>
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }

    /// <summary>true si la placa se giró 90° (su ancho de diseño quedó en Y).</summary>
    public bool Rotated { get; init; }

    /// <summary>¿Cabe en el recorrido de la máquina con la orientación elegida?</summary>
    public bool FitsMachine { get; set; } = true;

    /// <summary>Cobre ya transformado (para la vista previa).</summary>
    public PathsD Copper { get; } = new();

    /// <summary>Anillos de aislamiento en orden de corte (cada anillo cerrado, sin repetir el punto inicial).</summary>
    public List<List<PointD>> Isolation { get; } = new();

    /// <summary>Barrenos por broca, en orden de taladrado.</summary>
    public SortedDictionary<double, List<PointD>> DrillGroups { get; } = new();

    /// <summary>Anillo del contorno de corte (cerrado, sin repetir el inicial); vacío si no hay contorno.</summary>
    public List<PointD> Contour { get; } = new();
    public List<double> ContourDepths { get; } = new();

    /// <summary>Puntos (coordenadas de trabajo) donde dos redes quedan más cerca que el ancho efectivo: ahí el aislamiento no las separa.</summary>
    public List<PointD> Bridges { get; } = new();

    /// <summary>Problemas que revisar antes de fresar (incluye los de la placa).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Hallazgos informativos (qué se detectó y qué se decidió).</summary>
    public List<string> Info { get; } = new();

    /// <summary>Archivos .nc generados, en orden de uso.</summary>
    public List<(string FileName, string Content)> Files { get; } = new();

    public int DrillCount => DrillGroups.Values.Sum(g => g.Count);

    /// <summary>Largo total de corte del aislamiento, en mm.</summary>
    public double IsolationLengthMm
    {
        get
        {
            double total = 0;
            foreach (var ring in Isolation)
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    total += double.Hypot(b.x - a.x, b.y - a.y);
                }
            return total;
        }
    }
}
