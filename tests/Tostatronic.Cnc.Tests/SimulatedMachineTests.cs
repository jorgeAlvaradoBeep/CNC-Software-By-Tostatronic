using Tostatronic.Cnc.Core.Config;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Machine;
using Tostatronic.Cnc.Core.Motion;
using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Tests;

public class SimulatedMachineTests
{
    [Fact]
    public void ElContadorDeSondaCreceAlBajarYSoloEsCoherenteCon3F()
    {
        // comportamiento del firmware real observado el 2026-10-21
        using var sim = new SimulatedMachine { CommandPeriodMs = 0 };
        var replies = new System.Collections.Concurrent.BlockingCollection<byte[]>();
        sim.DataReceived += d => replies.Add(d);
        sim.Open();
        long start = sim.ProbeCounter;
        var down = new StepCommand(0, 0, -14, false);
        var up = new StepCommand(0, 0, 14, false);

        sim.Write(PacketEncoder.EncodeMove(PacketEncoder.ProbeHeader, down, down, down));
        Assert.True(replies.TryTake(out var r1, 2000));
        Assert.Equal(new byte[] { 1 }, r1);
        Assert.Equal(start + 42, sim.ProbeCounter);   // bajar 42 pasos ⇒ +42

        sim.Write(PacketEncoder.EncodeMove(PacketEncoder.ProbeHeader, up, up, up));
        Assert.True(replies.TryTake(out _, 2000));
        Assert.Equal(start, sim.ProbeCounter);        // subir con 0x3F lo regresa

        sim.Write(PacketEncoder.EncodeMove(PacketEncoder.MoveHeader, up, down, up));
        Assert.True(replies.TryTake(out _, 2000));
        Assert.Equal(start - 42, sim.ProbeCounter);   // con 0xFF todo paso de Z resta
    }

    private static async Task<MachineController> ConnectAsync()
    {
        var controller = new MachineController(new MachineSettings()) { SimulationMode = true };
        Assert.True(await controller.ConnectAsync());
        controller.Simulator!.CommandPeriodMs = 0; // pruebas a toda velocidad
        return controller;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ConectaYDetectaElSimulador()
    {
        using var c = await ConnectAsync();
        Assert.Equal(MachineStatus.Inactivo, c.Status);
        c.Disconnect();
        Assert.Equal(MachineStatus.SinConexion, c.Status);
    }

    [Fact]
    public async Task UnTrabajoCompletoDejaLaPosicionExacta()
    {
        using var c = await ConnectAsync();
        string file = Path.Combine(Path.GetTempPath(), "trabajo_prueba.nc");
        File.WriteAllText(file, """
            G21 G90
            G0 Z2.000
            M3
            G1 Z-0.5 F50
            G1 X10 Y5 F150
            G0 Z2.000
            G0 X0 Y0
            M5
            M2
            """);
        c.LoadProgram(file);
        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);

        // termina en X0 Y0 Z2: 0 / 0 / 500 pasos
        Assert.Equal(0, c.XSteps);
        Assert.Equal(0, c.YSteps);
        Assert.Equal(500, c.ZSteps);
        // el simulador (posición física) coincide con los contadores
        Assert.Equal(0, c.Simulator!.XSteps);
        Assert.Equal(0, c.Simulator.YSteps);
        Assert.Equal(500, c.Simulator.ZSteps);
        Assert.False(c.Simulator.SpindleOn);
    }

    [Fact]
    public async Task ElHusilloSeEnciendeYApagaConM3M5()
    {
        using var c = await ConnectAsync();
        string file = Path.Combine(Path.GetTempPath(), "husillo_prueba.nc");
        File.WriteAllText(file, """
            M3
            G1 X2 F150
            M5
            M2
            """);
        c.LoadProgram(file);
        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);
        Assert.False(c.Simulator!.SpindleOn);
        Assert.Equal(500, c.Simulator.XSteps); // 2 mm
    }

    [Fact]
    public async Task ElToqueDeSondaDetieneYFijaZ0()
    {
        using var c = await ConnectAsync();
        // la placa simulada está 5 mm abajo; pedir bajar 10 mm debe tocar y fijar Z0
        await c.JogStepAsync(Axis.Z, -1, 10.0);
        Assert.Equal(0, c.ZSteps);               // Z0 quedó en el contacto
        Assert.True(c.ProbeLatch);               // y solo se permite subir
        Assert.Equal(-1250, c.Simulator!.ZSteps); // la física quedó en la superficie (-5 mm)

        // bajar de nuevo está bloqueado por la sonda
        await c.JogStepAsync(Axis.Z, -1, 1.0);
        Assert.Equal(0, c.ZSteps);

        // subir sí se permite y limpia el candado
        await c.JogStepAsync(Axis.Z, +1, 1.0);
        Assert.False(c.ProbeLatch);
        Assert.Equal(250, c.ZSteps);
    }

    [Fact]
    public async Task ElJogPorPasosMueveExacto()
    {
        using var c = await ConnectAsync();
        await c.JogStepAsync(Axis.X, +1, 10.0);
        await c.JogStepAsync(Axis.Y, -1, 0.1);
        Assert.Equal(2500, c.XSteps);
        Assert.Equal(-25, c.YSteps);
        Assert.Equal(c.XSteps, c.Simulator!.XSteps);
        Assert.Equal(c.YSteps, c.Simulator.YSteps);
    }

    [Fact]
    public async Task GoZeroRegresaAlCeroDeTrabajo()
    {
        using var c = await ConnectAsync();
        await c.JogStepAsync(Axis.X, +1, 5.0);
        await c.JogStepAsync(Axis.Z, +1, 2.0);
        c.ZeroAll(); // nuevo cero aquí
        await c.JogStepAsync(Axis.X, +1, 3.0);
        await c.JogStepAsync(Axis.Z, +1, 1.0);
        await c.GoToZeroAsync();
        Assert.Equal(0, c.XSteps);
        Assert.Equal(0, c.YSteps);
        Assert.Equal(0, c.ZSteps);
        // física: el cero quedó en X5 Z2 absolutos
        Assert.Equal(1250, c.Simulator!.XSteps);
        Assert.Equal(500, c.Simulator.ZSteps);
    }

    [Fact]
    public async Task PausaYReanudaConRegresoAPosicion()
    {
        using var c = await ConnectAsync();
        c.Simulator!.CommandPeriodMs = 1; // lento para alcanzar a pausar
        string file = Path.Combine(Path.GetTempPath(), "pausa_prueba.nc");
        File.WriteAllText(file, """
            G0 X40
            G0 X0
            M2
            """);
        c.LoadProgram(file);
        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Ejecutando && c.XSteps > 500);
        c.PauseJob();
        await WaitForAsync(() => c.Status == MachineStatus.Pausado);
        long pausedX = c.XSteps;

        // mover la máquina durante la pausa
        c.Simulator.CommandPeriodMs = 0;
        await c.JogStepAsync(Axis.Z, +1, 2.0);
        Assert.Equal(500, c.ZSteps);

        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Terminado);
        // terminó en X0 y la Z del jog se regresó a la de la pausa (0)
        Assert.Equal(0, c.XSteps);
        Assert.Equal(0, c.ZSteps);
        Assert.Equal(0, c.Simulator.XSteps);
        Assert.Equal(0, c.Simulator.ZSteps);
    }

    [Fact]
    public async Task ElBotonDeLaMaquinaDetieneElTrabajo()
    {
        using var c = await ConnectAsync();
        c.Simulator!.CommandPeriodMs = 1;
        string file = Path.Combine(Path.GetTempPath(), "liberada_prueba.nc");
        File.WriteAllText(file, "G0 X40\nM2\n");
        c.LoadProgram(file);
        c.StartOrResumeJob();
        await WaitForAsync(() => c.Status == MachineStatus.Ejecutando && c.XSteps > 100);
        c.Simulator.PressReleaseButton();
        await WaitForAsync(() => c.Status == MachineStatus.Detenido);
        Assert.False(c.SpindleOn);
    }
}
