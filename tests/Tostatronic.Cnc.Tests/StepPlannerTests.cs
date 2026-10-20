using Tostatronic.Cnc.Core.Motion;
using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Tests;

public class StepPlannerTests
{
    private static (long x, long y, long z, List<StepCommand> cmds) Run(
        StepPlanner planner, StepPosition from, StepPosition to, double advance)
    {
        var cmds = planner.Linear(from, to, advance, false).ToList();
        long x = 0, y = 0, z = 0;
        foreach (var c in cmds)
        {
            Assert.InRange(c.Dx, -14, 14);
            Assert.InRange(c.Dy, -14, 14);
            Assert.InRange(c.Dz, -14, 14);
            x += c.Dx; y += c.Dy; z += c.Dz;
        }
        return (x, y, z, cmds);
    }

    [Theory]
    [InlineData(100, 0, 0)]
    [InlineData(0, -250, 0)]
    [InlineData(37, 91, -13)]
    [InlineData(-1000, 333, 7)]
    [InlineData(1, 1, 1)]
    [InlineData(2500, 1250, -25)]
    public void LaSumaDeComandosEsExacta(long dx, long dy, long dz)
    {
        var planner = new StepPlanner();
        var (x, y, z, _) = Run(planner, StepPosition.Zero, new StepPosition(dx, dy, dz), 14.0);
        Assert.Equal(dx, x);
        Assert.Equal(dy, y);
        Assert.Equal(dz, z);
    }

    [Fact]
    public void AvanceLentoEmiteComandosEnCeroComoRelleno()
    {
        var planner = new StepPlanner();
        var (x, _, _, cmds) = Run(planner, StepPosition.Zero, new StepPosition(10, 0, 0), 0.5);
        Assert.Equal(10, x);
        // a 0.5 pasos/comando se requieren ~20 comandos; la mitad son de relleno
        Assert.InRange(cmds.Count, 15, 40);
        Assert.Contains(cmds, c => c.IsZeroMove);
    }

    [Fact]
    public void ElFrenadoSuavizaLaInversion()
    {
        var planner = new StepPlanner();
        var (x1, _, _, _) = Run(planner, StepPosition.Zero, new StepPosition(200, 0, 0), 14.0);
        Assert.Equal(200, x1);
        // regreso: el primer comando tras la inversión no puede ser -14 de golpe
        var back = planner.Linear(new StepPosition(200, 0, 0), StepPosition.Zero, 14.0, false).ToList();
        Assert.True(Math.Abs(back[0].Dx) < 14, $"primer comando tras inversión fue {back[0].Dx}");
        Assert.Equal(-200, back.Sum(c => (long)c.Dx));
    }

    [Fact]
    public void LaAceleracionEsGradualDesdeElReposo()
    {
        var planner = new StepPlanner();
        var cmds = planner.Linear(StepPosition.Zero, new StepPosition(300, 0, 0), 14.0, false).ToList();
        // arranca con un comando ≤ 10 (regla de frenado de Wegstr) y sube de a poco
        Assert.True(Math.Abs(cmds[0].Dx) <= 10, $"primer comando fue {cmds[0].Dx}");
        Assert.Equal(300, cmds.Sum(c => (long)c.Dx));
    }

    [Fact]
    public void MovimientoNuloNoEmiteNada()
    {
        var planner = new StepPlanner();
        Assert.Empty(planner.Linear(new StepPosition(5, 5, 5), new StepPosition(5, 5, 5), 14.0, false));
    }
}
