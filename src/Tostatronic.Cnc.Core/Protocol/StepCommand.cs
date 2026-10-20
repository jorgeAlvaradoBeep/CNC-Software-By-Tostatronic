namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Un micro-movimiento del protocolo Wegstr: deltas en pasos de motor (−14..+14 por eje)
/// y el estado del husillo, que viaja en cada comando.
/// </summary>
public readonly record struct StepCommand(int Dx, int Dy, int Dz, bool Spindle)
{
    public static readonly StepCommand Zero = new(0, 0, 0, false);

    public bool IsZeroMove => Dx == 0 && Dy == 0 && Dz == 0;
}
