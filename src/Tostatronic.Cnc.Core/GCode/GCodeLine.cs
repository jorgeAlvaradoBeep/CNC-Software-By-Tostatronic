namespace Tostatronic.Cnc.Core.GCode;

/// <summary>Una línea de G-code ya parseada. Las coordenadas ausentes (null) significan "sin cambio".</summary>
public sealed class GCodeLine
{
    /// <summary>Comando normalizado: "G00", "G01", "G02", "G03", "G04", "G81"…, "M00", "M03", "M05", "M06", "M47", "M02"…</summary>
    public string Command { get; set; } = "";

    public double? X, Y, Z, I, J, R, F, P, S;

    /// <summary>Número N de la línea (0 si no trae).</summary>
    public int N { get; set; }

    /// <summary>Índice de la línea en el archivo original (base 0), para progreso y errores.</summary>
    public int SourceIndex { get; set; }

    public string Raw { get; set; } = "";

    public bool IsRapid => Command is "G00";
    public bool IsLinearCut => Command is "G01";
    public bool IsArc => Command is "G02" or "G03";
    public bool IsDrillCycle => Command is "G73" or "G81" or "G82" or "G83";
    public bool IsMotion => IsRapid || IsLinearCut || IsArc;

    public override string ToString() => Raw;
}

public sealed class GCodeException : Exception
{
    public int SourceIndex { get; }

    public GCodeException(string message, int sourceIndex, string raw)
        : base($"Línea {sourceIndex + 1}: {message}  [{raw}]")
    {
        SourceIndex = sourceIndex;
    }
}
