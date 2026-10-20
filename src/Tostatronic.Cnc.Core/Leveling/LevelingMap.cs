namespace Tostatronic.Cnc.Core.Leveling;

/// <summary>Celda de la rejilla de calibración (columna desde xmin, fila desde ymax, como Wegstr).</summary>
public readonly record struct GridCell(int Col, int Row);

/// <summary>Un punto a sondear: la celda y la posición exacta (en pasos) donde el corte la pisa primero.</summary>
public readonly record struct LevelingPoint(GridCell Cell, long XSteps, long YSteps);

/// <summary>
/// Mapa de calibración de altura estilo Wegstr (ver docs/wegstr-analisis.md §4): una rejilla
/// sobre el bounding box del programa cuyos puntos de sondeo son el primer punto donde la
/// trayectoria de corte pisa cada celda, en el orden del recorrido. Tras sondear guarda el
/// desnivel medido por celda en pasos de Z, con signo positivo = superficie más alta que la
/// referencia en X0 Y0.
/// </summary>
public sealed class LevelingMap
{
    private readonly Dictionary<GridCell, int> _offsets = new();
    private readonly HashSet<GridCell> _seen = new();
    private readonly List<LevelingPoint> _points = new();

    public LevelingMap(double rasterMm, long cellStepsX, long cellStepsY, long minXSteps, long maxYSteps, double mmPerStepZ)
    {
        RasterMm = rasterMm;
        CellStepsX = Math.Max(1, cellStepsX);
        CellStepsY = Math.Max(1, cellStepsY);
        MinXSteps = minXSteps;
        MaxYSteps = maxYSteps;
        MmPerStepZ = mmPerStepZ;
    }

    public double RasterMm { get; }
    public long CellStepsX { get; }
    public long CellStepsY { get; }
    public long MinXSteps { get; }
    public long MaxYSteps { get; }
    public double MmPerStepZ { get; }

    /// <summary>Puntos a sondear, en el orden en que el G-code recorre la placa.</summary>
    public IReadOnlyList<LevelingPoint> ProbePoints => _points;

    public int OffsetCount => _offsets.Count;
    public bool HasOffsets => _offsets.Count > 0;
    public double MinOffsetMm { get; private set; }
    public double MaxOffsetMm { get; private set; }

    public GridCell CellOf(long xSteps, long ySteps) =>
        new((int)((xSteps - MinXSteps) / CellStepsX), (int)((MaxYSteps - ySteps) / CellStepsY));

    /// <summary>Registra la posición de un micro-comando de corte; la primera por celda queda como punto de sondeo.</summary>
    public void RegisterCutPoint(long xSteps, long ySteps)
    {
        var cell = CellOf(xSteps, ySteps);
        if (_seen.Add(cell))
            _points.Add(new LevelingPoint(cell, xSteps, ySteps));
    }

    public void SetOffset(GridCell cell, int steps)
    {
        _offsets[cell] = steps;
        double mm = steps * MmPerStepZ;
        if (mm < MinOffsetMm) MinOffsetMm = mm;
        if (mm > MaxOffsetMm) MaxOffsetMm = mm;
    }

    public bool TryGetOffset(GridCell cell, out int steps) => _offsets.TryGetValue(cell, out steps);

    /// <summary>Desniveles medidos por celda (para guardar el mapa).</summary>
    public IReadOnlyDictionary<GridCell, int> Offsets => _offsets;
}
