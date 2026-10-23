using Tostatronic.Cnc.Core.Config;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Leveling;
using Tostatronic.Cnc.Core.Machine;

namespace Tostatronic.Cnc.Tests;

public class LevelingMapTests
{
    private static LevelingMap Map() => new(1.0, 250, 250, 0, 2500, 0.004);

    [Fact]
    public void LaCeldaSigueLaFormulaDeWegstr()
    {
        var map = Map();
        Assert.Equal(new GridCell(0, 0), map.CellOf(0, 2500));   // esquina superior izquierda
        Assert.Equal(new GridCell(1, 0), map.CellOf(260, 2400));
        Assert.Equal(new GridCell(0, 10), map.CellOf(0, 0));     // las filas crecen hacia abajo
    }

    [Fact]
    public void SoloLaPrimeraPosicionDeCadaCeldaEsPuntoDeSondeo()
    {
        var map = Map();
        map.RegisterCutPoint(10, 2490);
        map.RegisterCutPoint(20, 2480);  // misma celda: se ignora
        map.RegisterCutPoint(260, 2490); // celda nueva
        Assert.Equal(2, map.ProbePoints.Count);
        Assert.Equal(10, map.ProbePoints[0].XSteps);
        Assert.Equal(260, map.ProbePoints[1].XSteps);
    }

    [Fact]
    public void LosDesnivelesLlevanMinimoYMaximoEnMm()
    {
        var map = Map();
        map.SetOffset(new GridCell(0, 0), -10);
        map.SetOffset(new GridCell(1, 0), 25);
        Assert.Equal(-0.04, map.MinOffsetMm, 3);
        Assert.Equal(0.10, map.MaxOffsetMm, 3);
        Assert.True(map.TryGetOffset(new GridCell(1, 0), out int v) && v == 25);
        Assert.False(map.TryGetOffset(new GridCell(5, 5), out _));
    }
}

public class LevelingMapFileTests
{
    [Fact]
    public void GuardaYCargaElMapaCompleto()
    {
        var map = new LevelingMap(1.0, 250, 250, -500, 2500, 0.004);
        map.SetOffset(new GridCell(0, 0), -10);
        map.SetOffset(new GridCell(3, 7), 25);
        string path = Path.Combine(Path.GetTempPath(), "mapa_prueba.nivel.json");
        LevelingMapFile.Save(map, path);

        var loaded = LevelingMapFile.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.OffsetCount);
        Assert.Equal(map.MinXSteps, loaded.MinXSteps);
        Assert.Equal(map.MaxYSteps, loaded.MaxYSteps);
        Assert.True(loaded.TryGetOffset(new GridCell(3, 7), out int v) && v == 25);
        Assert.Equal(map.MinOffsetMm, loaded.MinOffsetMm, 6);
        Assert.Equal(map.MaxOffsetMm, loaded.MaxOffsetMm, 6);
    }

    [Fact]
    public void UnArchivoInexistenteODanadoDevuelveNull()
    {
        Assert.Null(LevelingMapFile.Load(Path.Combine(Path.GetTempPath(), "no_existe.nivel.json")));
        string bad = Path.Combine(Path.GetTempPath(), "mapa_roto.nivel.json");
        File.WriteAllText(bad, "{ esto no es un mapa ");
        Assert.Null(LevelingMapFile.Load(bad));
    }
}

public class ProbePointPlannerTests
{
    private static LevelingMap Build(string gcode) =>
        ProbePointPlanner.Build(GCodeProgram.FromText(gcode), new MachineSettings());

    [Fact]
    public void UnCorteRectoDaUnPuntoPorCelda()
    {
        var map = Build("""
            G00 X0 Y0
            G01 Z-0.1 F100
            G01 X5 F150
            M30
            """);
        // plunge en la celda 0 + traverse que pisa las celdas 1..5 (raster 1 mm)
        Assert.Equal(6, map.ProbePoints.Count);
        Assert.All(map.ProbePoints, p => Assert.Equal(0, p.YSteps));
        Assert.Equal(0, map.ProbePoints[0].XSteps);
        Assert.Equal(1250, map.ProbePoints[^1].XSteps);
        for (int i = 1; i < map.ProbePoints.Count; i++)
            Assert.True(map.ProbePoints[i].XSteps > map.ProbePoints[i - 1].XSteps);
    }

    [Fact]
    public void LosG00NoGeneranPuntos()
    {
        var map = Build("""
            G00 X5 Y5
            G00 X0 Y0
            G01 Z-0.1 F100
            M30
            """);
        Assert.Single(map.ProbePoints);
        Assert.Equal(0, map.ProbePoints[0].XSteps);
        Assert.Equal(0, map.ProbePoints[0].YSteps);
    }

    [Fact]
    public void ElCicloDeTaladradoSondeaElBarreno()
    {
        var map = Build("""
            G00 Z1
            G81 X3 Y2 Z-1.8 R1 F30
            M30
            """);
        Assert.Single(map.ProbePoints);
        Assert.Equal(750, map.ProbePoints[0].XSteps);
        Assert.Equal(500, map.ProbePoints[0].YSteps);
    }

    [Fact]
    public void UnArchivoRealGeneraPuntosDentroDelBoundingBox()
    {
        var program = GCodeProgram.Load(Fixtures.Example("Bascula_1_bottom_aislamiento.nc"));
        var s = new MachineSettings();
        var map = ProbePointPlanner.Build(program, s);
        Assert.True(map.ProbePoints.Count > 50, $"Solo {map.ProbePoints.Count} puntos.");
        long minX = s.StepsFromMm(Core.Motion.Axis.X, program.MinX) - 1;
        long maxX = s.StepsFromMm(Core.Motion.Axis.X, program.MaxX) + 1;
        long minY = s.StepsFromMm(Core.Motion.Axis.Y, program.MinY) - 1;
        long maxY = s.StepsFromMm(Core.Motion.Axis.Y, program.MaxY) + 1;
        Assert.All(map.ProbePoints, p =>
        {
            Assert.InRange(p.XSteps, minX, maxX);
            Assert.InRange(p.YSteps, minY, maxY);
        });
    }
}

public class LevelingCompensatorTests
{
    private static LevelingMap Map() => new(1.0, 250, 250, 0, 2500, 0.004);

    [Fact]
    public void EnCorteXYConvergeAUnPasoPorComando()
    {
        var map = Map();
        map.SetOffset(map.CellOf(0, 2500), 5);
        var comp = new LevelingCompensator(map, 0);
        for (int i = 0; i < 5; i++)
            Assert.Equal(1, comp.Extra(0, 2500, 0, rampStep: true));
        Assert.Equal(0, comp.Extra(0, 2500, 0, rampStep: true));
        Assert.Equal(5, comp.AppliedSteps);
    }

    [Fact]
    public void EnPlungeAplicaTodoElDesnivel()
    {
        var map = Map();
        map.SetOffset(map.CellOf(0, 2500), 25);
        var comp = new LevelingCompensator(map, 0);
        // el comando nominal baja 2 pasos: caben 14−(−2)... |Dz| ≤ 14 ⇒ extra máx 16
        Assert.Equal(16, comp.Extra(0, 2500, -2, rampStep: false));
        Assert.Equal(9, comp.Extra(0, 2500, -2, rampStep: false));
        Assert.Equal(0, comp.Extra(0, 2500, -2, rampStep: false));
        Assert.Equal(25, comp.AppliedSteps);
    }

    [Fact]
    public void AlInvertirElAjusteEmiteLaHolgura()
    {
        var map = Map();
        map.SetOffset(map.CellOf(0, 2500), 3);   // celda A: subir 3
        map.SetOffset(map.CellOf(260, 2500), 1); // celda B: bajar 2
        var comp = new LevelingCompensator(map, backlashSteps: 4);
        Assert.Equal(1, comp.Extra(0, 2500, 0, true));
        Assert.Equal(1, comp.Extra(0, 2500, 0, true));
        Assert.Equal(1, comp.Extra(0, 2500, 0, true));
        // inversión: 1 paso de ajuste + 4 de holgura que el juego mecánico absorbe
        Assert.Equal(-5, comp.Extra(260, 2500, 0, true));
        Assert.Equal(-1, comp.Extra(260, 2500, 0, true));
        Assert.Equal(0, comp.Extra(260, 2500, 0, true));
        Assert.Equal(1, comp.AppliedSteps);
    }

    [Fact]
    public void UnaCeldaSinDatoNoAjusta()
    {
        var map = Map();
        map.SetOffset(map.CellOf(0, 2500), 5);
        var comp = new LevelingCompensator(map, 0);
        Assert.Equal(0, comp.Extra(10_000, 2500, 0, true)); // celda nunca sondeada
        Assert.Equal(0, comp.AppliedSteps);
    }

    [Fact]
    public void LosExtrasNoEnviadosRegresanAPendientes()
    {
        var map = Map();
        map.SetOffset(map.CellOf(0, 2500), 2);
        var comp = new LevelingCompensator(map, 0);
        Assert.Equal(1, comp.Extra(0, 2500, 0, true));
        comp.ReturnUnsent(1); // el comando se perdió
        // sale el ajuste nuevo + el pendiente devuelto (la rampa solo limita ajustes nuevos)
        Assert.Equal(2, comp.Extra(0, 2500, 0, true));
        Assert.Equal(0, comp.Extra(0, 2500, 0, true));
        Assert.Equal(2, comp.AppliedSteps);
    }
}

public class AutoLevelSimulationTests
{
    private static async Task<MachineController> ConnectAsync()
    {
        var controller = new MachineController(new MachineSettings()) { SimulationMode = true };
        Assert.True(await controller.ConnectAsync());
        controller.Simulator!.CommandPeriodMs = 0;
        return controller;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(20);
        }
    }

    private static string WriteProgram(string name, string gcode)
    {
        string file = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllText(file, gcode);
        return file;
    }

    [Fact]
    public async Task LaCalibracionMideLaPendienteDeLaPlaca()
    {
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = (x, _) => -2.0 + 0.02 * x; // 5 pasos de desnivel por mm en X
        c.LoadProgram(WriteProgram("nivel_prueba.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            G0 Z2
            M2
            """));
        await c.AutoLevelAsync();

        var map = c.Leveling;
        Assert.NotNull(map);
        Assert.True(c.ApplyLeveling);
        Assert.Equal(11, map!.ProbePoints.Count);          // celdas 0..10 del traverse
        Assert.Equal(map.ProbePoints.Count, map.OffsetCount);

        Assert.True(map.TryGetOffset(map.CellOf(0, 0), out int o0));
        Assert.InRange(o0, -1, 1);                          // la referencia está en X0 Y0
        Assert.True(map.TryGetOffset(map.CellOf(2500, 0), out int o10));
        Assert.InRange(o10, 48, 52);                        // +0.2 mm = +50 pasos a 10 mm
        Assert.InRange(map.MaxOffsetMm, 0.19, 0.21);

        // termina en X0 Y0 Z2, con Z0 redefinido en el contacto de referencia
        Assert.Equal(0, c.XSteps);
        Assert.Equal(0, c.YSteps);
        Assert.Equal(500, c.ZSteps);
        Assert.Equal(0, c.Simulator.ZSteps);                // física: 2 mm sobre la placa (−2 mm)
        Assert.Equal(MachineStatus.Inactivo, c.Status);
    }

    [Fact]
    public async Task ElTrabajoCompensadoSigueLaSuperficie()
    {
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = (x, _) => -2.0 + 0.02 * x;
        c.LoadProgram(WriteProgram("nivel_trabajo.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            M2
            """));
        await c.AutoLevelAsync();
        Assert.NotNull(c.Leveling);

        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);

        Assert.Equal(2500, c.XSteps);
        // física: la herramienta terminó ≈ 0.1 mm bajo la superficie local, no bajo la de X0
        long surfSteps = (long)Math.Round((-2.0 + 0.02 * 10.0) / 0.004); // −450
        long depth = c.Simulator.ZSteps - surfSteps;
        Assert.InRange(depth, -33, -17); // −25 pasos (0.1 mm) ± rezago de la rampa
        // el contador y la física siguen sincronizados (el cero quedó 2 mm bajo el arranque)
        Assert.Equal(c.Simulator.ZSteps, c.ZSteps - 500);
    }

    [Fact]
    public async Task SinAplicarElMapaElTrabajoVaPlano()
    {
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = (x, _) => -2.0 + 0.02 * x;
        c.LoadProgram(WriteProgram("nivel_plano.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            M2
            """));
        await c.AutoLevelAsync();
        c.ApplyLeveling = false;

        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);

        Assert.Equal(2500, c.XSteps);
        Assert.Equal(-25, c.ZSteps);                 // Z nominal, sin compensación
        Assert.Equal(-525, c.Simulator.ZSteps);      // física = contador − 500
    }

    [Fact]
    public async Task LaCalibracionSoloUsaPaquetesDeSondeo()
    {
        // Wegstr mueve TODO el auto level con 0x3F (subidas y traslados incluidos) porque el
        // contador de Z del firmware solo es coherente con esa cabecera (hardware 2026-10-21).
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = (x, _) => -2.0 + 0.02 * x;
        var headers = new List<byte>();
        c.RawTraffic += (data, outgoing) =>
        {
            if (outgoing && data.Length == 7) lock (headers) headers.Add(data[0]);
        };
        c.LoadProgram(WriteProgram("nivel_3f.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            M2
            """));
        await c.AutoLevelAsync();
        Assert.NotNull(c.Leveling);
        lock (headers)
        {
            Assert.NotEmpty(headers);
            Assert.All(headers, h => Assert.Equal(0x3F, h));
        }
    }

    [Fact]
    public async Task LaCalibracionSeReanudaTrasPerderLaSenal()
    {
        var c = new MachineController(new MachineSettings()) { SimulationMode = true, ResponseTimeoutMs = 150 };
        Assert.True(await c.ConnectAsync());
        c.Simulator!.CommandPeriodMs = 0;
        c.Simulator.SurfaceZMm = (x, _) => -2.0 + 0.02 * x;
        int touches = 0;
        c.RawTraffic += (data, outgoing) =>
        {
            // la máquina se cuelga tras el 5.º toque (referencia + 4 puntos), como pasó en hardware
            if (!outgoing && data.Length == 3 && data[0] == 4 && ++touches == 5)
                c.Simulator.Silent = true;
        };
        c.LoadProgram(WriteProgram("nivel_reanuda.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            M2
            """));
        await c.AutoLevelAsync();
        Assert.Null(c.Leveling);
        Assert.True(c.HasPausedCalibration);
        Assert.Equal(MachineStatus.Inactivo, c.Status);

        c.Simulator.Silent = false;
        await c.AutoLevelAsync();   // continúa donde se quedó, volviendo a referenciar en X0 Y0
        Assert.False(c.HasPausedCalibration);
        var map = c.Leveling;
        Assert.NotNull(map);
        Assert.Equal(11, map!.OffsetCount);
        Assert.True(map.TryGetOffset(map.CellOf(2500, 0), out int o10));
        Assert.InRange(o10, 48, 52);
        Assert.Equal(0, c.XSteps);
        Assert.Equal(0, c.YSteps);
        Assert.Equal(500, c.ZSteps);
        Assert.Equal(0, c.Simulator.ZSteps);
        c.Dispose();
    }

    [Fact]
    public async Task LosG0TambienSiguenLaSuperficie()
    {
        // Wegstr aplica la rampa de ±1 paso también en los G00 en XY (hardware 2026-10-21)
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = (x, _) => -2.0 + 0.02 * x;
        c.LoadProgram(WriteProgram("nivel_g0.nc", """
            G0 X0 Y0
            G1 Z-0.1 F100
            G1 X10 F150
            G0 X0
            M2
            """));
        await c.AutoLevelAsync();
        Assert.NotNull(c.Leveling);

        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);

        Assert.Equal(0, c.XSteps);
        // el regreso en G0 también se corrigió: termina ≈ 0.1 mm bajo la superficie en X0 (−500 pasos)
        long depth = c.Simulator.ZSteps - (-500);
        Assert.InRange(depth, -33, -17);
    }

    [Fact]
    public async Task SinContactoLaCalibracionFallaConAviso()
    {
        using var c = await ConnectAsync();
        c.Simulator!.SurfaceZMm = null; // la sonda nunca toca
        string? warning = null;
        c.Warning += w => warning = w;
        c.LoadProgram(WriteProgram("nivel_sin_toque.nc", """
            G1 Z-0.1 F100
            M2
            """));
        await c.AutoLevelAsync();
        Assert.Null(c.Leveling);
        Assert.False(c.ApplyLeveling);
        Assert.Contains("sin contacto", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MachineStatus.Inactivo, c.Status);
    }

    [Fact]
    public async Task SinMovimientosDeCorteNoHayQueSondear()
    {
        using var c = await ConnectAsync();
        c.LoadProgram(WriteProgram("nivel_sin_corte.nc", """
            G0 X5 Y5
            G0 X0 Y0
            M2
            """));
        string? warning = null;
        c.Warning += w => warning = w;
        await c.AutoLevelAsync();
        Assert.Null(c.Leveling);
        Assert.Contains("no tiene movimientos de corte", warning);
    }
}
