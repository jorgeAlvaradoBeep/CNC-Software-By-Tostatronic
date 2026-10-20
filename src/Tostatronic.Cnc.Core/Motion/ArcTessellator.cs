namespace Tostatronic.Cnc.Core.Motion;

/// <summary>
/// Discretiza arcos G2/G3 (plano XY, centro por I/J) en puntos intermedios con tolerancia de
/// cuerda. Si el radio inicial y final difieren un poco (G-code impreciso) se interpola el radio.
/// Inicio == fin se interpreta como círculo completo, igual que la convención usual.
/// </summary>
public static class ArcTessellator
{
    public static IEnumerable<(double X, double Y)> Tessellate(
        double x0, double y0, double x1, double y1,
        double i, double j, bool clockwise, double tolMm = 0.005)
    {
        double cx = x0 + i, cy = y0 + j;
        double r0 = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        double r1 = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
        double rMax = Math.Max(r0, r1);
        if (rMax < 1e-9)
            yield break;

        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        double sweep = a1 - a0;
        if (clockwise)
        {
            if (sweep >= -1e-9) sweep -= 2 * Math.PI;
        }
        else
        {
            if (sweep <= 1e-9) sweep += 2 * Math.PI;
        }

        double dTheta = 2 * Math.Acos(Math.Clamp(1 - tolMm / rMax, -1, 1));
        if (dTheta < 1e-4) dTheta = 1e-4;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / dTheta));

        for (int k = 1; k < n; k++)
        {
            double a = a0 + sweep * k / n;
            double r = r0 + (r1 - r0) * k / n;
            yield return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        yield return (x1, y1); // terminar exactamente en el punto final
    }
}
