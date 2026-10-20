using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Core.Motion;

/// <summary>
/// Convierte movimientos lineales (en pasos) en la secuencia de micro-comandos del protocolo,
/// replicando el comportamiento de Wegstr: interpolación 3D con redondeo acumulado,
/// máximo ±14 pasos por comando, comandos en cero como relleno de tiempo a avances bajos,
/// y limitación de aceleración/inversión por eje (frenado).
/// Es un objeto con estado: recuerda el último comando emitido entre segmentos del mismo trabajo.
/// </summary>
public sealed class StepPlanner
{
    private int _prevX, _prevY, _prevZ;

    public void Reset() => _prevX = _prevY = _prevZ = 0;

    /// <summary>
    /// Genera los comandos para ir de <paramref name="from"/> a <paramref name="to"/>.
    /// <paramref name="advance"/> = distancia 3D (en pasos) a recorrer por comando, evaluada
    /// en cada comando (permite cambiar la velocidad en vivo).
    /// </summary>
    public IEnumerable<StepCommand> Linear(StepPosition from, StepPosition to, Func<double> advance, Func<bool> spindle)
    {
        long dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
        if (dist < 1e-9)
            yield break;

        long ex = 0, ey = 0, ez = 0; // pasos ya emitidos por eje
        double done = 0;             // distancia ya recorrida

        while (true)
        {
            // el tramo termina cuando se recorrió la distancia y los pasos emitidos son exactos
            if (done >= dist - 1e-9 && ex == dx && ey == dy && ez == dz)
                yield break;

            double adv = Math.Max(advance(), 0.1);
            if (done + adv > dist)
                adv = dist - done;

            int cx, cy, cz;
            int guard = 0;
            while (true)
            {
                double t = done + adv;
                cx = Clamp15((int)(Math.Round(dx / dist * t) - ex));
                cy = Clamp15((int)(Math.Round(dy / dist * t) - ey));
                cz = Clamp15((int)(Math.Round(dz / dist * t) - ez));

                if (Math.Abs(cx) > 14 || Math.Abs(cy) > 14 || Math.Abs(cz) > 14)
                    throw new InvalidOperationException($"Más de 14 pasos por comando ({cx},{cy},{cz}); avance {adv:F2}.");

                if (guard++ < 1000)
                {
                    // frenado: al acelerar o invertir un eje se recorta el avance y se reintenta;
                    // el comando en cero resultante es la pausa de frenado
                    double pen = Brake(cx, _prevX);
                    if (pen == 0) pen = Brake(cy, _prevY);
                    if (pen == 0) pen = Brake(cz, _prevZ);
                    if (pen > 0)
                    {
                        adv = Math.Max(adv - pen, 0);
                        continue;
                    }
                }
                break;
            }

            _prevX = cx; _prevY = cy; _prevZ = cz;
            ex += cx; ey += cy; ez += cz;
            done += adv;
            yield return new StepCommand(cx, cy, cz, spindle());
        }
    }

    public IEnumerable<StepCommand> Linear(StepPosition from, StepPosition to, double advance, bool spindle)
        => Linear(from, to, () => advance, () => spindle);

    private static int Clamp15(int v) => v switch { 15 => 14, -15 => -14, _ => v };

    /// <summary>
    /// Penalización de frenado de Wegstr: 0 cuando el comando mantiene el sentido sin crecer
    /// (desacelerar es gratis); 1.0 en saltos grandes (>10) y 0.5 en crecimientos a alta velocidad.
    /// </summary>
    internal static double Brake(int n, int prev)
    {
        bool smooth = (n >= 0 && prev >= n) || (n <= 0 && prev <= n);
        if (smooth) return 0;
        if (Math.Abs(n - prev) > 10) return 1.0;
        if (Math.Abs(n - prev) > 1 && Math.Abs(n) > 10) return 0.5;
        return 0;
    }
}
