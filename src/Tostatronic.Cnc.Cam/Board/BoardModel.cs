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

    /// <summary>CAD que exportó los archivos, deducido de los nombres ("EasyEDA", "Altium Designer", "KiCad", "desconocido").</summary>
    public string Source { get; set; } = "desconocido";

    /// <summary>Cobre de cada cara (unidades de Clipper); null si la capa no vino.</summary>
    public Paths64? CopperTop { get; set; }
    public Paths64? CopperBottom { get; set; }

    /// <summary>Pads (flashes) de cada cara, en mm de diseño; para los chequeos de barrenos.</summary>
    public List<GerberFlash> PadsTop { get; } = new();
    public List<GerberFlash> PadsBottom { get; } = new();

    /// <summary>¿La cara dibuja pistas o regiones, o solo trae pads?</summary>
    public bool TopHasTraces { get; set; }
    public bool BottomHasTraces { get; set; }

    /// <summary>Contorno de la placa como polígono(s) cerrado(s); vacío si no se pudo encadenar.</summary>
    public Paths64 Outline { get; set; } = new();

    /// <summary>true si no vino capa de contorno y se usó el rectángulo del cobre más un margen.</summary>
    public bool OutlineSynthetic { get; set; }

    public List<DrillHole> Drills { get; } = new();

    /// <summary>Islas de cobre que quedaron fuera de la placa (en mm de diseño), descartadas o solo avisadas.</summary>
    public List<StrayCopper> Stray { get; } = new();

    /// <summary>Límites de la placa en mm (del contorno; si no hay, del cobre más el margen).</summary>
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }

    public double WidthMm => MaxX - MinX;
    public double HeightMm => MaxY - MinY;

    /// <summary>Problemas que hay que revisar antes de fresar.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Hallazgos informativos de la validación (qué se detectó y qué se decidió).</summary>
    public List<string> Info { get; } = new();

    public bool HasFace(BoardFace face) =>
        face == BoardFace.Bottom ? CopperBottom is { Count: > 0 } : CopperTop is { Count: > 0 };

    /// <summary>
    /// ¿Es una placa de una sola capa? Lo es cuando a lo más una cara trae pistas; la otra, si viene,
    /// trae solo los pads de los componentes pasantes (que todo CAD proyecta a ambas caras).
    /// </summary>
    public bool IsSingleLayer => !(TopHasTraces && BottomHasTraces);

    /// <summary>La cara que conviene fresar: la que tiene pistas; si las dos (o ninguna), la inferior si existe.</summary>
    public BoardFace PreferredFace =>
        TopHasTraces && !BottomHasTraces ? BoardFace.Top
        : !TopHasTraces && BottomHasTraces ? BoardFace.Bottom
        : HasFace(BoardFace.Bottom) ? BoardFace.Bottom : BoardFace.Top;

    public bool FaceHasTraces(BoardFace face) => face == BoardFace.Bottom ? BottomHasTraces : TopHasTraces;
}

/// <summary>Cara a fresar. La inferior se fresa en espejo (vista desde el cobre).</summary>
public enum BoardFace { Bottom, Top }

/// <summary>Una isla de cobre lejos del resto de la placa (coordenadas de diseño, mm).</summary>
public readonly record struct StrayCopper(double MinX, double MinY, double MaxX, double MaxY, double DistanceMm)
{
    public double CenterX => (MinX + MaxX) / 2;
    public double CenterY => (MinY + MaxY) / 2;
    public double WidthMm => MaxX - MinX;
    public double HeightMm => MaxY - MinY;
}
