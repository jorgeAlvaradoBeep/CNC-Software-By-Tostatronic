namespace Tostatronic.Cnc.Core.Motion;

/// <summary>
/// Conversión de avance (mm/min) a "pasos de distancia por comando", igual que Wegstr:
/// F máximo 170 mm/min ↔ 14 pasos por comando al 100 % de velocidad.
/// De ahí se infiere la cadencia del firmware: ≈ 50.6 comandos/s.
/// </summary>
public static class SpeedModel
{
    public const double MaxFeedMmMin = 170.0;
    public const double MaxStepsPerCommand = 14.0;
    public const double FeedDivisor = MaxFeedMmMin / MaxStepsPerCommand; // 12.142857...
    public const double CommandsPerSecond = 50.59;

    /// <summary>Avance de corte (G1/G2/G3): proporcional al F modal, recortado a [0.1, 14].</summary>
    public static double CuttingAdvance(double feedMmMin, int speedPercent)
    {
        double f = Math.Min(feedMmMin, MaxFeedMmMin);
        double v = f * (speedPercent / 100.0) / FeedDivisor;
        return Math.Clamp(v, 0.1, MaxStepsPerCommand);
    }

    /// <summary>
    /// Avance de un rápido (G0): el eje dominante va a 14 pasos/comando (al 100 %).
    /// Devuelve la distancia 3D recorrida por comando para ese tramo.
    /// </summary>
    public static double RapidAdvance(double distSteps, long dx, long dy, long dz, int speedPercent)
    {
        double maxAxis = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
        if (maxAxis == 0) return MaxStepsPerCommand;
        double perCmd = MaxStepsPerCommand * speedPercent / 100.0;
        return Math.Max(0.1, perCmd / maxAxis * distSteps);
    }

    /// <summary>Avance máximo del jog (14 pasos/comando al 100 %).</summary>
    public static double JogAdvance(int speedPercent) =>
        Math.Max(0.1, MaxStepsPerCommand * speedPercent / 100.0);
}
