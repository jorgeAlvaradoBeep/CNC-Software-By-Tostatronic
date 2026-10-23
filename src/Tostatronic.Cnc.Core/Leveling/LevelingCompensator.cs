namespace Tostatronic.Cnc.Core.Leveling;

/// <summary>
/// Aplica el mapa de alturas a un trabajo en curso (docs/wegstr-analisis.md §4.3): en
/// movimientos XY (G00 y G01 por igual, confirmado en hardware el 2026-10-21) el ajuste de Z
/// converge al desnivel de la celda actual a razón de ±1 paso por comando (sin interpolación
/// bilineal, igual que Wegstr); en movimientos puros de Z (plunge) el desnivel se aplica
/// completo. Cada vez que el sentido del ajuste se invierte se emiten pasos extra de holgura
/// (backlash) que el juego mecánico del husillo de Z absorbe sin movimiento real.
/// El estado vive durante todo el trabajo (una instancia por trabajo).
/// </summary>
public sealed class LevelingCompensator
{
    private readonly LevelingMap _map;
    private readonly int _backlash;
    private long _applied;  // desnivel ya decidido (pasos de Z; + = subir)
    private long _shift;    // pasos de holgura emitidos (los absorbe el juego mecánico)
    private long _pending;  // pasos decididos que todavía no caben en un comando
    private int _lastDir;   // sentido del último ajuste, para detectar inversiones

    public LevelingCompensator(LevelingMap map, int backlashSteps)
    {
        _map = map;
        _backlash = Math.Clamp(backlashSteps, 0, 30);
    }

    public int PointCount => _map.OffsetCount;

    /// <summary>Desnivel lógico aplicado hasta ahora, en pasos (sin contar la holgura).</summary>
    public long AppliedSteps => _applied;

    /// <summary>
    /// Pasos de Z extra que debe llevar el comando de corte que termina en (x, y).
    /// Con <paramref name="rampStep"/> el ajuste nuevo se limita a 1 paso por comando (cortes
    /// XY); sin él se decide completo (plunge). Lo decidido que no cabe en el comando
    /// (|Dz| ≤ 14) queda pendiente y sale en los comandos de corte siguientes.
    /// </summary>
    public int Extra(long xSteps, long ySteps, int nominalDz, bool rampStep)
    {
        if (_map.TryGetOffset(_map.CellOf(xSteps, ySteps), out int target) && target != _applied)
        {
            int dir = Math.Sign(target - _applied);
            if (_lastDir != 0 && dir != _lastDir && _backlash > 0)
            {
                _pending += (long)_backlash * dir;
                _shift += (long)_backlash * dir;
            }
            long step = rampStep ? dir : target - _applied;
            _applied += step;
            _pending += step;
            _lastDir = dir;
        }
        int give = (int)Math.Clamp(_pending, -14L - nominalDz, 14L - nominalDz);
        _pending -= give;
        return give;
    }

    /// <summary>Regresa a pendientes los extras de comandos que nunca llegaron a la máquina.</summary>
    public void ReturnUnsent(long extraSteps) => _pending += extraSteps;
}
