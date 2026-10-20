namespace Tostatronic.Cnc.Core.Motion;

public enum Axis { X, Y, Z }

/// <summary>Posición en pasos de motor, relativa al cero de trabajo.</summary>
public readonly record struct StepPosition(long X, long Y, long Z)
{
    public static readonly StepPosition Zero = new(0, 0, 0);

    public StepPosition With(Axis axis, long value) => axis switch
    {
        Axis.X => this with { X = value },
        Axis.Y => this with { Y = value },
        _ => this with { Z = value },
    };

    public long Get(Axis axis) => axis switch { Axis.X => X, Axis.Y => Y, _ => Z };
}
