namespace Tostatronic.Cnc.Core.GCode;

/// <summary>Un archivo de G-code cargado: líneas crudas + parseo y validación completos.</summary>
public sealed class GCodeProgram
{
    public string Name { get; }
    public string FilePath { get; }
    public IReadOnlyList<string> RawLines { get; }
    public IReadOnlyList<GCodeLine> Lines { get; }

    /// <summary>Límites XY del recorrido en mm (incluye puntos intermedios de los arcos).</summary>
    public double MinX { get; private set; }
    public double MaxX { get; private set; }
    public double MinY { get; private set; }
    public double MaxY { get; private set; }
    public double MinZ { get; private set; }

    private GCodeProgram(string name, string filePath, IReadOnlyList<string> rawLines, IReadOnlyList<GCodeLine> lines)
    {
        Name = name;
        FilePath = filePath;
        RawLines = rawLines;
        Lines = lines;
        ComputeBounds();
    }

    public static GCodeProgram Load(string path)
    {
        var raw = File.ReadAllLines(path);
        return FromLines(raw, Path.GetFileName(path), path);
    }

    public static GCodeProgram FromText(string text, string name = "(texto)")
        => FromLines(text.Replace("\r\n", "\n").Split('\n'), name, "");

    private static GCodeProgram FromLines(IReadOnlyList<string> raw, string name, string path)
    {
        var parser = new GCodeParser();
        var lines = new List<GCodeLine>();
        for (int i = 0; i < raw.Count; i++)
        {
            var line = parser.ParseLine(raw[i], i);
            if (line != null)
                lines.Add(line);
        }
        if (lines.Count == 0)
            throw new GCodeException("El archivo no contiene movimientos ni comandos.", 0, name);
        return new GCodeProgram(name, path, raw, lines);
    }

    private void ComputeBounds()
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = 0;
        double x = 0, y = 0;
        bool any = false;

        void Point(double px, double py)
        {
            any = true;
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        Point(0, 0); // el recorrido arranca en el cero de trabajo
        foreach (var l in Lines)
        {
            if (l.IsArc)
            {
                foreach (var (px, py) in Motion.ArcTessellator.Tessellate(
                             x, y, l.X ?? x, l.Y ?? y, l.I!.Value, l.J!.Value, l.Command == "G02"))
                    Point(px, py);
            }
            if (l.IsMotion || l.IsDrillCycle)
            {
                if (l.X != null || l.Y != null)
                    Point(l.X ?? x, l.Y ?? y);
                x = l.X ?? x;
                y = l.Y ?? y;
            }
            if (l.Z is double z && z < minZ) minZ = z;
            if (l.IsDrillCycle && l.Z is double zc && zc < minZ) minZ = zc;
        }
        if (!any) { minX = maxX = minY = maxY = 0; }
        MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY; MinZ = minZ;
    }
}

/// <summary>
/// Iterador de ejecución sobre el programa: expande los ciclos de taladrado G73/G81/G82/G83
/// en movimientos simples (como Wegstr) y soporta reinicio (M47 / rebobinar).
/// </summary>
public sealed class GCodeStream
{
    private readonly GCodeProgram _program;
    private readonly Queue<GCodeLine> _pendingExpansion = new();
    private int _index;
    private double? _clearanceZ; // última Z antes del ciclo, para el retorno

    public GCodeStream(GCodeProgram program) => _program = program;

    /// <summary>Índice (base 0) de la línea fuente del último elemento devuelto.</summary>
    public int CurrentSourceIndex { get; private set; }

    public int ParsedCount => _program.Lines.Count;
    public int ParsedPosition => _index;

    public void Rewind()
    {
        _index = 0;
        _pendingExpansion.Clear();
        _clearanceZ = null;
        CurrentSourceIndex = 0;
    }

    public GCodeLine? Next()
    {
        if (_pendingExpansion.Count > 0)
            return _pendingExpansion.Dequeue();

        if (_index >= _program.Lines.Count)
            return null;

        var line = _program.Lines[_index++];
        CurrentSourceIndex = line.SourceIndex;

        if (line.IsDrillCycle && line.Z != null && line.R != null)
        {
            // expansión del ciclo: XY en rápido, bajar a R, taladrar a Z con F, volver arriba
            _pendingExpansion.Enqueue(new GCodeLine { Command = "G00", Z = line.R, SourceIndex = line.SourceIndex, Raw = line.Raw });
            _pendingExpansion.Enqueue(new GCodeLine { Command = "G01", Z = line.Z, F = line.F, SourceIndex = line.SourceIndex, Raw = line.Raw });
            _pendingExpansion.Enqueue(new GCodeLine { Command = "G00", Z = _clearanceZ ?? line.R, SourceIndex = line.SourceIndex, Raw = line.Raw });
            return new GCodeLine { Command = "G00", X = line.X, Y = line.Y, SourceIndex = line.SourceIndex, Raw = line.Raw };
        }

        if (line.Z != null)
            _clearanceZ = line.Z;

        return line;
    }
}
