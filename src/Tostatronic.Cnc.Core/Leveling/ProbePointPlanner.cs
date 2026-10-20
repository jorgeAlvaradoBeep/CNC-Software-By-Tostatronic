using Tostatronic.Cnc.Core.Config;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Motion;

namespace Tostatronic.Cnc.Core.Leveling;

/// <summary>
/// Preproceso del auto level (docs/wegstr-analisis.md §4.1): recorre el programa igual que lo
/// hará el trabajo (ciclos de taladrado expandidos, arcos teselados) y registra la posición de
/// cada micro-comando de CORTE — nunca de un G00 —; la primera posición que pisa cada celda de
/// la rejilla queda como punto de sondeo. Así los puntos caen sobre las pistas reales y en el
/// orden del recorrido.
/// </summary>
public static class ProbePointPlanner
{
    public static LevelingMap Build(GCodeProgram program, MachineSettings s)
    {
        double raster = Math.Clamp(s.AutoLevelRasterMm, 0.5, 70.0);
        var map = new LevelingMap(
            raster,
            s.StepsFromMm(Axis.X, raster),
            s.StepsFromMm(Axis.Y, raster),
            s.StepsFromMm(Axis.X, program.MinX),
            s.StepsFromMm(Axis.Y, program.MaxY),
            s.MmPerStep(Axis.Z));

        var stream = new GCodeStream(program);
        var planner = new StepPlanner();
        var pos = StepPosition.Zero;

        GCodeLine? line;
        while ((line = stream.Next()) != null)
        {
            switch (line.Command)
            {
                case "G00":
                    pos = TargetOf(line, pos, s);
                    break;
                case "G01":
                    pos = Walk(map, planner, pos, TargetOf(line, pos, s));
                    break;
                case "G02":
                case "G03":
                {
                    double x0 = s.MmFromSteps(Axis.X, pos.X);
                    double y0 = s.MmFromSteps(Axis.Y, pos.Y);
                    double z0 = s.MmFromSteps(Axis.Z, pos.Z);
                    double z1 = line.Z ?? z0;
                    var points = ArcTessellator
                        .Tessellate(x0, y0, line.X ?? x0, line.Y ?? y0, line.I!.Value, line.J!.Value, line.Command == "G02")
                        .ToList();
                    for (int k = 0; k < points.Count; k++)
                    {
                        double zk = z0 + (z1 - z0) * (k + 1) / points.Count;
                        pos = Walk(map, planner, pos, new StepPosition(
                            s.StepsFromMm(Axis.X, points[k].X),
                            s.StepsFromMm(Axis.Y, points[k].Y),
                            s.StepsFromMm(Axis.Z, zk)));
                    }
                    break;
                }
                case "M47": // reiniciar el programa: con una pasada ya se pisaron todas las celdas
                case "M02":
                case "M30":
                    return map;
            }
        }
        return map;
    }

    private static StepPosition TargetOf(GCodeLine l, StepPosition pos, MachineSettings s) => new(
        l.X is double x ? s.StepsFromMm(Axis.X, x) : pos.X,
        l.Y is double y ? s.StepsFromMm(Axis.Y, y) : pos.Y,
        l.Z is double z ? s.StepsFromMm(Axis.Z, z) : pos.Z);

    private static StepPosition Walk(LevelingMap map, StepPlanner planner, StepPosition from, StepPosition to)
    {
        var p = from;
        foreach (var cmd in planner.Linear(from, to, 14.0, false))
        {
            p = new StepPosition(p.X + cmd.Dx, p.Y + cmd.Dy, p.Z + cmd.Dz);
            map.RegisterCutPoint(p.X, p.Y);
        }
        return to;
    }
}
