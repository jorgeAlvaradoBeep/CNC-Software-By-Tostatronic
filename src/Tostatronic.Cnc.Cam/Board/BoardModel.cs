using Clipper2Lib;
using Tostatronic.Cnc.Cam.Gerber;

namespace Tostatronic.Cnc.Cam.Board;

/// <summary>
/// Una placa cargada de los archivos de fabricación, en coordenadas de diseño (mm, sin espejo
/// ni traslado): cobre por cara, contorno como polígono cerrado, barrenos y límites.
/// </summary>
public sealed class BoardModel
{
    public string Name { get; set; } = "placa";

    /// <summary>Cobre de cada cara (unidades de Clipper); null si la capa no vino.</summary>
    public Paths64? CopperTop { get; set; }
    public Paths64? CopperBottom { get; set; }

    /// <summary>Contorno de la placa como polígono(s) cerrado(s); vacío si no se pudo encadenar.</summary>
    public Paths64 Outline { get; set; } = new();

    public List<DrillHole> Drills { get; } = new();

    /// <summary>Límites de la placa en mm (del contorno; si no hay, del cobre).</summary>
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }

    public double WidthMm => MaxX - MinX;
    public double HeightMm => MaxY - MinY;

    public List<string> Warnings { get; } = new();

    public bool HasFace(BoardFace face) =>
        face == BoardFace.Bottom ? CopperBottom is { Count: > 0 } : CopperTop is { Count: > 0 };
}

/// <summary>Cara a fresar. La inferior se fresa en espejo (vista desde el cobre).</summary>
public enum BoardFace { Bottom, Top }
