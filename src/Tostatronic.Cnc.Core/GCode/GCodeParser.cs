using System.Globalization;

namespace Tostatronic.Cnc.Core.GCode;

/// <summary>
/// Parser modal de G-code, compatible con el dialecto que acepta Wegstr (ver análisis §5):
/// comentarios entre paréntesis, G17 ignorado, comando de movimiento modal (línea con solo
/// coordenadas usa el último G), arcos con I/J obligatorios, X/Y/Z/I/J/R modales para arcos
/// y ciclos, G18/G19 rechazados. Además se acepta G04 (pausa de P segundos), que Wegstr ignora.
/// </summary>
public sealed class GCodeParser
{
    private static readonly string[] MotionCommands = { "G00", "G01", "G02", "G03", "G73", "G81", "G82", "G83" };
    private static readonly string[] IgnoredCommands =
    {
        // códigos de preparación comunes en postprocesadores que no cambian nada aquí
        "G20", "G21", "G40", "G49", "G54", "G80", "G90", "G94", "G71", "G91.1", "G43",
    };

    private double? _x, _y, _z, _i, _j, _r;
    private string _modalMotion = "";

    public void Reset()
    {
        _x = _y = _z = _i = _j = _r = null;
        _modalMotion = "";
    }

    /// <summary>Parsea una línea. Devuelve null si no hay nada que ejecutar.</summary>
    public GCodeLine? ParseLine(string raw, int sourceIndex)
    {
        string s = StripComments(raw.ToUpperInvariant().Replace(" ", "").Replace("\t", ""));
        if (s.Length == 0)
            return null;

        var line = new GCodeLine { Raw = raw.Trim(), SourceIndex = sourceIndex };

        string? n = TakeNumber(s, 'N');
        if (n != null && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln))
            line.N = ln;

        string command = "";
        string? g = TakeNumber(s, 'G');
        if (g != null)
        {
            command = "G" + NormalizeCode(g);
            if (MotionCommands.Contains(command))
                _modalMotion = command;
        }
        else
        {
            string? m = TakeNumber(s, 'M');
            if (m != null)
                command = "M" + NormalizeCode(m);
        }

        line.X = TakeDouble(s, 'X');
        line.Y = TakeDouble(s, 'Y');
        line.Z = TakeDouble(s, 'Z');
        line.I = TakeDouble(s, 'I');
        line.J = TakeDouble(s, 'J');
        line.R = TakeDouble(s, 'R');
        line.F = TakeDouble(s, 'F');
        line.P = TakeDouble(s, 'P');
        line.S = TakeDouble(s, 'S');

        // memoria modal de coordenadas (para arcos y ciclos)
        if (line.X != null) _x = line.X;
        if (line.Y != null) _y = line.Y;
        if (line.Z != null) _z = line.Z;
        if (line.I != null) _i = line.I;
        if (line.J != null) _j = line.J;
        if (line.R != null) _r = line.R;

        if (command is "G17")
            command = ""; // selección de plano XY: sin efecto
        if (command is "G18" or "G19")
            throw new GCodeException("Selección de plano no soportada (solo G17/plano XY).", sourceIndex, line.Raw);

        // línea con solo coordenadas: usa el movimiento modal
        bool hasCoords = line.X != null || line.Y != null || line.Z != null ||
                         line.I != null || line.J != null || line.R != null;
        if (command.Length == 0 && hasCoords)
            command = _modalMotion.Length > 0 ? _modalMotion : "G00";

        if (command.Length == 0)
            return null;

        line.Command = command;

        if (line.IsArc)
        {
            line.X ??= _x; line.Y ??= _y; line.Z ??= _z;
            line.I ??= _i; line.J ??= _j;
            if (line.I == null || line.J == null)
                throw new GCodeException("Falta el parámetro I/J del arco.", sourceIndex, line.Raw);
        }
        else if (line.IsDrillCycle)
        {
            line.R ??= _r;
            line.Z ??= _z;
        }

        if (IgnoredCommands.Contains(command))
            return null;

        // una G de preparación sin coordenadas ni F ni P no ejecuta nada
        if (command[0] == 'G' && !line.IsMotion && !line.IsDrillCycle && command != "G04"
            && line.X == null && line.Y == null && line.Z == null && line.F == null)
            return null;

        return line;
    }

    private static string StripComments(string s)
    {
        while (true)
        {
            int open = s.IndexOf('(');
            if (open < 0) break;
            int close = s.IndexOf(')', open);
            if (close < 0) { s = s[..open]; break; }
            s = s.Remove(open, close - open + 1);
        }
        int semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi];
        return s.Trim();
    }

    /// <summary>"0"→"00", "1"→"01", "2.0"→"02", "91.1"→"91.1"</summary>
    private static string NormalizeCode(string code)
    {
        if (double.TryParse(code, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && Math.Abs(v - Math.Round(v)) < 1e-9)
            return ((int)Math.Round(v)).ToString("00", CultureInfo.InvariantCulture);
        return code;
    }

    private static string? TakeNumber(string s, char letter)
    {
        int idx = s.IndexOf(letter);
        if (idx < 0) return null;
        int start = idx + 1;
        int end = start;
        while (end < s.Length)
        {
            char c = s[end];
            if ((c == '-' && end == start) || c == '.' || c == ',' || char.IsDigit(c))
                end++;
            else
                break;
        }
        return end > start ? s[start..end].Replace(',', '.') : null;
    }

    private static double? TakeDouble(string s, char letter)
    {
        string? t = TakeNumber(s, letter);
        if (t == null) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
