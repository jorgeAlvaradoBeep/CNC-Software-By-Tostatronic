using Tostatronic.Cnc.Core.Config;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Leveling;
using Tostatronic.Cnc.Core.Motion;
using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Core.Machine;

/// <summary>La calibración de altura se canceló por la máquina o por falta de contacto.</summary>
internal sealed class ProbeAbortException : Exception
{
    public ProbeAbortException(string message) : base(message) { }
}

/// <summary>
/// Orquestador de la máquina: conexión, contadores de posición (lado PC, como Wegstr),
/// jog continuo y por pasos, ceros de trabajo, husillo, GO ZERO, trabajos de G-code,
/// keep-alive y paro de emergencia.
/// </summary>
public sealed class MachineController : IDisposable
{
    private WegstrLink? _link;
    private ISerialLink? _serial;
    private CancellationTokenSource _estopCts = new();
    private readonly SemaphoreSlim _activity = new(1, 1);
    private readonly System.Timers.Timer _keepAlive = new(1000) { AutoReset = true };
    private bool _keepAliveBusy;
    private bool _noSignalWarned;

    // jog continuo
    private readonly int[] _jogDir = new int[3];
    private Task? _jogTask;

    public MachineSettings Settings { get; }

    public MachineController(MachineSettings? settings = null)
    {
        Settings = settings ?? MachineSettings.Load();
        _keepAlive.Elapsed += (_, _) => _ = KeepAliveTickAsync();
    }

    // ---- estado observable ----

    public long XSteps { get; private set; }
    public long YSteps { get; private set; }
    public long ZSteps { get; private set; }
    public StepPosition Position => new(XSteps, YSteps, ZSteps);
    public double Xmm => Settings.MmFromSteps(Axis.X, XSteps);
    public double Ymm => Settings.MmFromSteps(Axis.Y, YSteps);
    public double Zmm => Settings.MmFromSteps(Axis.Z, ZSteps);

    /// <summary>Bandera lógica del husillo; viaja en cada comando que se manda.</summary>
    public bool SpindleOn { get; private set; }

    public int SpeedPercent { get; set; } = 100;
    public MachineStatus Status { get; private set; } = MachineStatus.SinConexion;
    public bool IsConnected => _link != null;
    public bool SimulationMode { get; set; }
    public SimulatedMachine? Simulator { get; private set; }

    /// <summary>Tras un toque de sonda solo se permite subir Z, hasta que se despegue.</summary>
    public bool ProbeLatch { get; private set; }

    public GCodeProgram? Program { get; private set; }
    public JobRunner? Job { get; private set; }

    /// <summary>Mapa de calibración de altura del programa actual (null = sin calibrar).</summary>
    public LevelingMap? Leveling { get; private set; }

    /// <summary>Aplicar el mapa de alturas a los trabajos (se toma al iniciar cada trabajo).</summary>
    public bool ApplyLeveling { get; set; }

    /// <summary>(puntos sondeados, total, desnivel del último punto en mm).</summary>
    public event Action<int, int, double>? LevelingProgress;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<string>? Warning;
    /// <summary>(datos, esSalida) — tráfico crudo para la consola detallada.</summary>
    public event Action<byte[], bool>? RawTraffic;

    internal void RaiseChanged() => Changed?.Invoke();
    internal void RaiseLog(string msg) => Log?.Invoke(msg);
    internal void RaiseWarning(string msg) => Warning?.Invoke(msg);

    internal void SetStatus(MachineStatus status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }

    internal void SetSpindleFlag(bool on)
    {
        if (SpindleOn == on) return;
        SpindleOn = on;
        Changed?.Invoke();
    }

    internal CancellationToken EstopToken => _estopCts.Token;
    internal bool TryAcquireActivity() => _activity.Wait(0);
    internal Task AcquireActivityAsync(CancellationToken ct) => _activity.WaitAsync(ct);

    internal void ReleaseActivity()
    {
        try { _activity.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
    internal bool ActivityFree => _activity.CurrentCount > 0;

    // ---- conexión ----

    public async Task<bool> ConnectAsync(string? portName = null)
    {
        Disconnect();
        ISerialLink serial;
        if (SimulationMode)
        {
            Simulator = new SimulatedMachine
            {
                StepsPer10mm = Settings.StepsPer10mmZ,
                // placa simulada 5 mm abajo del punto de arranque, con una ligera inclinación
                // para que la calibración de altura tenga algo que medir
                SurfaceZMm = (x, y) => -5.0 + 0.012 * x - 0.008 * y,
            };
            serial = Simulator;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                RaiseWarning("Selecciona un puerto o usa Buscar.");
                return false;
            }
            serial = new SerialPortLink(portName);
        }

        try
        {
            serial.Open();
            var wl = new WegstrLink(serial);
            wl.RawSent += d => RawTraffic?.Invoke(d, true);
            wl.RawReceived += d => RawTraffic?.Invoke(d, false);
            wl.NotificationReceived += OnNotification;

            var reply = await wl.SendAsync(PortScanner.DetectionPacket()).ConfigureAwait(false);
            _serial = serial;
            _link = wl;
            _estopCts.Dispose();
            _estopCts = new CancellationTokenSource();
            _noSignalWarned = false;
            SetStatus(MachineStatus.Inactivo);
            RaiseLog($"Conectado a {serial.Name} (respuesta {(byte)reply.Kind}).");
            if (reply.Kind == MachineNotification.SpindleOverload)
                RaiseWarning("La máquina reporta husillo sobrecargado.");
            if (reply.Kind == MachineNotification.Released)
                RaiseWarning("La máquina está liberada (botón físico).");
            await SendSettingsAsync().ConfigureAwait(false);
            _keepAlive.Start();
            return true;
        }
        catch (MachineTimeoutException)
        {
            serial.Dispose();
            RaiseWarning($"No hay una Wegstr en {serial.Name} (no respondió).");
            return false;
        }
        catch (Exception ex)
        {
            serial.Dispose();
            RaiseWarning($"No se pudo abrir {serial.Name}: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> SearchPortsAsync()
    {
        var ports = SerialPortLink.GetPortNames();
        if (ports.Length == 0)
        {
            RaiseLog("No hay puertos seriales en el sistema.");
            return null;
        }
        return await PortScanner.FindMachineAsync(ports, n => new SerialPortLink(n), RaiseLog).ConfigureAwait(false);
    }

    public void Disconnect()
    {
        _keepAlive.Stop();
        StopAllJog();
        _estopCts.Cancel();
        Job?.Discard();
        Job = Program != null ? new JobRunner(this, Program) : null;
        if (_link != null)
        {
            _link.NotificationReceived -= OnNotification;
            _link.Dispose();
            _link = null;
        }
        _serial?.Dispose();
        _serial = null;
        Simulator = null;
        SetSpindleFlag(false);
        SetStatus(MachineStatus.SinConexion);
        Changed?.Invoke();
    }

    private void OnNotification(MachineReply reply)
    {
        // los bucles activos reciben la respuesta por SendAsync; aquí solo se reportan
        // los avisos que llegan con la máquina ociosa (p. ej. botón físico)
        if (!ActivityFree) return;
        switch (reply.Kind)
        {
            case MachineNotification.SpindleOverload:
                SetSpindleFlag(false);
                RaiseWarning("Husillo sobrecargado.");
                break;
            case MachineNotification.Released:
                SetSpindleFlag(false);
                RaiseWarning("Máquina liberada con el botón físico.");
                break;
        }
    }

    private async Task SendSettingsAsync()
    {
        if (_link == null) return;
        await _link.SendAsync(PacketEncoder.EncodeSettings(Settings.Performance), _estopCts.Token).ConfigureAwait(false);
    }

    private async Task KeepAliveTickAsync()
    {
        if (_keepAliveBusy || _link == null || !ActivityFree) return;
        _keepAliveBusy = true;
        try
        {
            await SendSettingsAsync().ConfigureAwait(false);
            if (_noSignalWarned)
            {
                _noSignalWarned = false;
                RaiseLog("La máquina volvió a responder.");
            }
        }
        catch (MachineTimeoutException)
        {
            if (!_noSignalWarned)
            {
                _noSignalWarned = true;
                RaiseWarning("La máquina no responde al keep-alive.");
            }
        }
        catch (Exception) { }
        finally { _keepAliveBusy = false; }
    }

    // ---- envío con contadores ----

    internal async Task<MachineReply> SendMoveInternalAsync(byte header, IReadOnlyList<StepCommand> cmds, CancellationToken ct)
    {
        if (_link == null) throw new InvalidOperationException("Sin conexión.");
        var reply = await _link.SendAsync(PacketEncoder.EncodeMove(header, cmds), ct).ConfigureAwait(false);
        if (reply.Kind is MachineNotification.Ack or MachineNotification.ProbeTouch)
        {
            long dx = 0, dy = 0, dz = 0;
            foreach (var c in cmds) { dx += c.Dx; dy += c.Dy; dz += c.Dz; }
            XSteps += dx; YSteps += dy; ZSteps += dz;
            Changed?.Invoke();
        }
        return reply;
    }

    /// <summary>
    /// Movimiento planeado hasta <paramref name="target"/> (rápido). Devuelve null si terminó,
    /// o la respuesta anómala (toque/liberada/sobrecarga) que lo interrumpió.
    /// </summary>
    internal async Task<MachineReply?> PlannedMoveAsync(StepPosition target, byte header, CancellationToken ct)
    {
        var planner = new StepPlanner();
        var from = Position;
        long dx = target.X - from.X, dy = target.Y - from.Y, dz = target.Z - from.Z;
        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
        if (dist < 1e-9) return null;
        double advance = SpeedModel.RapidAdvance(dist, dx, dy, dz, SpeedPercent);

        var buffer = new List<StepCommand>(3);
        foreach (var cmd in planner.Linear(from, target, () => advance, () => SpindleOn))
        {
            buffer.Add(cmd);
            if (buffer.Count == 3)
            {
                var reply = await SendMoveInternalAsync(header, buffer, ct).ConfigureAwait(false);
                buffer.Clear();
                if (!reply.IsAck) return reply;
            }
        }
        if (buffer.Count > 0)
        {
            while (buffer.Count < 3) buffer.Add(new StepCommand(0, 0, 0, SpindleOn));
            var reply = await SendMoveInternalAsync(header, buffer, ct).ConfigureAwait(false);
            if (!reply.IsAck) return reply;
        }
        return null;
    }

    // ---- ceros ----

    public void ZeroAxis(Axis axis)
    {
        switch (axis)
        {
            case Axis.X: XSteps = 0; break;
            case Axis.Y: YSteps = 0; break;
            case Axis.Z: ZSteps = 0; break;
        }
        RaiseLog($"Cero de trabajo {axis} fijado.");
        Changed?.Invoke();
    }

    public void ZeroAll()
    {
        XSteps = YSteps = ZSteps = 0;
        RaiseLog("Cero de trabajo X Y Z fijado.");
        Changed?.Invoke();
    }

    // ---- husillo ----

    public async Task SetSpindleAsync(bool on)
    {
        if (!IsConnected) return;
        SetSpindleFlag(on);
        if (!TryAcquireActivity())
        {
            RaiseLog($"Husillo marcado {(on ? "encendido" : "apagado")}; se aplica con el siguiente movimiento.");
            return;
        }
        try
        {
            var cmd = new StepCommand(0, 0, 0, on);
            await SendMoveInternalAsync(PacketEncoder.ProbeHeader, new[] { cmd, cmd, cmd }, _estopCts.Token).ConfigureAwait(false);
            RaiseLog(on ? "Husillo encendido." : "Husillo apagado.");
        }
        catch (MachineTimeoutException) { RaiseWarning("La máquina no respondió al husillo."); }
        catch (OperationCanceledException) { }
        finally { ReleaseActivity(); }
    }

    // ---- jog ----

    private bool AnyJog => _jogDir[0] != 0 || _jogDir[1] != 0 || _jogDir[2] != 0;

    public void StartJog(Axis axis, int direction)
    {
        if (!IsConnected || direction == 0) return;
        if (Status is MachineStatus.Ejecutando or MachineStatus.Regresando or MachineStatus.YendoACero
            or MachineStatus.EsperandoHerramienta or MachineStatus.Calibrando)
            return;
        if (ProbeLatch)
        {
            if (axis == Axis.Z && direction > 0)
            {
                ProbeLatch = false;
                Changed?.Invoke();
            }
            else
            {
                RaiseWarning("Sonda en contacto: primero sube Z (Z+).");
                return;
            }
        }
        _jogDir[(int)axis] = Math.Sign(direction);
        if (_jogTask is null or { IsCompleted: true })
            _jogTask = Task.Run(JogLoopAsync);
    }

    public void StopJog(Axis axis) => _jogDir[(int)axis] = 0;

    public void StopAllJog()
    {
        _jogDir[0] = _jogDir[1] = _jogDir[2] = 0;
    }

    private async Task JogLoopAsync()
    {
        if (!TryAcquireActivity()) { StopAllJog(); return; }
        var prev = Status;
        SetStatus(MachineStatus.Jog);
        try
        {
            var ct = _estopCts.Token;
            double ramp = 0;
            double accX = 0, accY = 0, accZ = 0;
            while (AnyJog && IsConnected)
            {
                double target = SpeedModel.JogAdvance(SpeedPercent);
                ramp = NextRamp(ramp, target);
                var cmds = new StepCommand[3];
                for (int k = 0; k < 3; k++)
                {
                    int sx = JogStep(ref accX, _jogDir[0], ramp);
                    int sy = JogStep(ref accY, _jogDir[1], ramp);
                    int sz = JogStep(ref accZ, _jogDir[2], ramp);
                    cmds[k] = new StepCommand(sx, sy, sz, SpindleOn);
                }
                var reply = await SendMoveInternalAsync(PacketEncoder.ProbeHeader, cmds, ct).ConfigureAwait(false);
                if (reply.Kind == MachineNotification.ProbeTouch)
                {
                    StopAllJog();
                    HandleProbeTouchManual();
                    break;
                }
                if (reply.Kind == MachineNotification.Released)
                {
                    StopAllJog();
                    SetSpindleFlag(false);
                    RaiseWarning("Máquina liberada con el botón físico; movimiento detenido.");
                    break;
                }
                if (reply.Kind == MachineNotification.SpindleOverload)
                {
                    StopAllJog();
                    SetSpindleFlag(false);
                    RaiseWarning("Husillo sobrecargado; movimiento detenido.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { StopAllJog(); }
        catch (MachineTimeoutException)
        {
            StopAllJog();
            RaiseWarning("La máquina no envió la señal durante el movimiento.");
        }
        catch (Exception ex)
        {
            StopAllJog();
            RaiseWarning("Error en el jog: " + ex.Message);
        }
        finally
        {
            if (Status == MachineStatus.Jog)
                SetStatus(IsConnected ? MachineStatus.Inactivo : MachineStatus.SinConexion);
            ReleaseActivity();
        }
    }

    private static int JogStep(ref double acc, int dir, double v)
    {
        if (dir == 0) { acc = 0; return 0; }
        acc += v;
        int s = (int)acc;
        acc -= s;
        return s * dir;
    }

    /// <summary>Rampa de aceleración del jog, como Wegstr: +3 hasta 10, luego +1 hasta la meta.</summary>
    internal static double NextRamp(double cur, double target)
    {
        if (cur + 3 <= 10 && cur + 3 <= target) return cur + 3;
        if (cur + 1 >= 10 && cur + 1 <= target) return cur + 1;
        if (target < 10) return target;
        return Math.Min(cur, target);
    }

    private void HandleProbeTouchManual()
    {
        ZSteps = 0;
        ProbeLatch = true;
        RaiseWarning("Toque de sonda: movimiento detenido y Z0 fijado en el contacto.");
        Changed?.Invoke();
    }

    /// <summary>Jog por paso fijo (0.01 / 0.1 / 1 / 10 mm).</summary>
    public async Task JogStepAsync(Axis axis, int direction, double mm)
    {
        if (!IsConnected || direction == 0) return;
        if (ProbeLatch)
        {
            if (axis == Axis.Z && direction > 0)
            {
                ProbeLatch = false;
                Changed?.Invoke();
            }
            else
            {
                RaiseWarning("Sonda en contacto: primero sube Z (Z+).");
                return;
            }
        }
        if (!TryAcquireActivity()) return;
        var prev = Status;
        SetStatus(MachineStatus.Jog);
        try
        {
            long delta = Settings.StepsFromMm(axis, mm) * Math.Sign(direction);
            var target = Position.With(axis, Position.Get(axis) + delta);
            var reply = await PlannedMoveAsync(target, PacketEncoder.ProbeHeader, _estopCts.Token).ConfigureAwait(false);
            if (reply?.Kind == MachineNotification.ProbeTouch)
                HandleProbeTouchManual();
            else if (reply?.Kind == MachineNotification.Released)
                RaiseWarning("Máquina liberada; movimiento detenido.");
            else if (reply?.Kind == MachineNotification.SpindleOverload)
                RaiseWarning("Husillo sobrecargado; movimiento detenido.");
        }
        catch (OperationCanceledException) { }
        catch (MachineTimeoutException) { RaiseWarning("La máquina no envió la señal durante el movimiento."); }
        finally
        {
            if (Status == MachineStatus.Jog)
                SetStatus(IsConnected ? MachineStatus.Inactivo : MachineStatus.SinConexion);
            ReleaseActivity();
        }
    }

    // ---- GO ZERO ----

    public async Task GoToZeroAsync()
    {
        if (!IsConnected) return;
        if (ProbeLatch)
        {
            RaiseWarning("Sonda en contacto: primero sube Z (Z+).");
            return;
        }
        if (!TryAcquireActivity()) { RaiseWarning("Hay otra operación en curso."); return; }
        SetStatus(MachineStatus.YendoACero);
        try
        {
            var ct = _estopCts.Token;
            if (Settings.GoZeroSafeHeightEnabled)
            {
                long safe = Settings.StepsFromMm(Axis.Z, Settings.GoZeroSafeHeightMm);
                if (ZSteps < safe)
                {
                    var r0 = await PlannedMoveAsync(Position with { Z = safe }, PacketEncoder.MoveHeader, ct).ConfigureAwait(false);
                    if (r0 != null) return;
                }
            }
            var r1 = await PlannedMoveAsync(Position with { X = 0, Y = 0 }, PacketEncoder.MoveHeader, ct).ConfigureAwait(false);
            if (r1 != null) return;
            await PlannedMoveAsync(Position with { Z = 0 }, PacketEncoder.MoveHeader, ct).ConfigureAwait(false);
            RaiseLog("En el cero de trabajo.");
        }
        catch (OperationCanceledException) { }
        catch (MachineTimeoutException) { RaiseWarning("La máquina no envió la señal yendo al cero."); }
        finally
        {
            if (Status == MachineStatus.YendoACero)
                SetStatus(IsConnected ? MachineStatus.Inactivo : MachineStatus.SinConexion);
            ReleaseActivity();
        }
    }

    // ---- trabajos ----

    public void LoadProgram(string path)
    {
        var program = GCodeProgram.Load(path);
        Program = program;
        // el mapa de alturas es por programa; si hay uno guardado se ofrece, pero no se
        // aplica solo: únicamente vale si la placa y los ceros son los mismos de cuando se sondeó
        Leveling = LevelingMapFile.Load(LevelingMapFile.PathFor(path));
        ApplyLeveling = false;
        if (Leveling != null)
            RaiseLog($"Mapa de alturas guardado encontrado ({Leveling.OffsetCount} puntos). " +
                     "Marca \"Aplicar al trabajo\" SOLO si la placa y los ceros son los mismos; si no, recalibra.");
        Job?.Discard();
        Job = new JobRunner(this, program);
        RaiseLog($"Cargado {program.Name}: {program.RawLines.Count} líneas, " +
                 $"X {program.MinX:F2}..{program.MaxX:F2} mm, Y {program.MinY:F2}..{program.MaxY:F2} mm, Z mín {program.MinZ:F2} mm.");
        Changed?.Invoke();
    }

    public void CloseProgram()
    {
        if (Job is { IsRunning: true })
        {
            RaiseWarning("Detén el trabajo antes de cerrar el archivo.");
            return;
        }
        Job?.Discard();
        Job = null;
        Program = null;
        Leveling = null;
        ApplyLeveling = false;
        Changed?.Invoke();
    }

    public void StartOrResumeJob()
    {
        if (!IsConnected || Job == null) return;
        if (ProbeLatch)
        {
            RaiseWarning("Sonda en contacto: primero sube Z (Z+).");
            return;
        }
        if (Job.IsFinished)
            RewindJob();
        if (!Job!.IsStarted)
        {
            if (!TryAcquireActivity()) { RaiseWarning("Hay otra operación en curso."); return; }
            Job.Start();
        }
        else
        {
            Job.Resume();
        }
    }

    public void PauseJob() => Job?.RequestPause();

    public void StopJob()
    {
        if (Job == null) return;
        Job.RequestStop();
    }

    public void RewindJob()
    {
        if (Job == null || Program == null) return;
        if (Job.IsRunning)
        {
            RaiseWarning("Pausa o detén el trabajo antes de reiniciarlo.");
            return;
        }
        Job.Discard();
        Job = new JobRunner(this, Program);
        if (IsConnected) SetStatus(MachineStatus.Inactivo);
        RaiseLog("Trabajo reiniciado al principio.");
        Changed?.Invoke();
    }

    // ---- calibración de altura (auto level) ----

    /// <summary>Compensador para el trabajo que está por iniciar (null si no hay mapa o no se aplica).</summary>
    internal LevelingCompensator? CreateCompensatorForJob()
        => Leveling is { HasOffsets: true } map && ApplyLeveling
            ? new LevelingCompensator(map, Settings.AutoLevelZBacklashSteps)
            : null;

    /// <summary>
    /// Secuencia de calibración de altura estilo Wegstr (docs/wegstr-analisis.md §4.2):
    /// sube 2 mm, va a X0 Y0, toma la referencia con la sonda (y ajusta Z0 al contacto),
    /// sondea cada punto del mapa en el orden del recorrido y termina en X0 Y0 Z2.
    /// </summary>
    public async Task AutoLevelAsync()
    {
        if (!IsConnected) { RaiseWarning("Conecta la máquina antes de calibrar."); return; }
        if (Program == null)
        {
            RaiseWarning("Carga un G-code antes de calibrar: los puntos se sondean sobre sus trayectorias de corte.");
            return;
        }
        if (Job is { IsStarted: true, IsFinished: false })
        {
            RaiseWarning("Detén y reinicia el trabajo antes de calibrar.");
            return;
        }
        if (ProbeLatch) { RaiseWarning("Sonda en contacto: primero sube Z (Z+)."); return; }
        if (!TryAcquireActivity()) { RaiseWarning("Hay otra operación en curso."); return; }

        SetStatus(MachineStatus.Calibrando);
        Leveling = null;
        ApplyLeveling = false;
        try
        {
            var map = ProbePointPlanner.Build(Program, Settings);
            if (map.ProbePoints.Count == 0)
            {
                RaiseWarning("El programa no tiene movimientos de corte: no hay puntos que sondear.");
                return;
            }
            var ct = _estopCts.Token;
            double mmZ = Settings.MmPerStep(Axis.Z);
            RaiseLog($"Calibración de altura: {map.ProbePoints.Count} puntos, raster {map.RasterMm:F1} mm. " +
                     "La máquina sondeará sobre las trayectorias de corte (pinza de la sonda al cobre).");
            SetSpindleFlag(false);

            long lift2 = Settings.StepsFromMm(Axis.Z, 2.0);
            long descend = Settings.StepsFromMm(Axis.Z, 10.0);
            long safeNear = Settings.StepsFromMm(Axis.Z, Math.Clamp(Settings.AutoLevelSafeZMm, 0.1, 40.0));
            long safeFar = Math.Max(safeNear, Settings.StepsFromMm(Axis.Z, 2.0));
            int sign = Settings.ProbeZReportInverted ? -1 : 1;

            // 1) levantar 2 mm y 2) al origen de trabajo
            await RapidAsync(Position with { Z = Position.Z + lift2 }, ct).ConfigureAwait(false);
            await RapidAsync(Position with { X = 0, Y = 0 }, ct).ConfigureAwait(false);

            // 3) referencia en X0 Y0: el contacto redefine Z0 (de ahí salen todos los desniveles)
            short r0 = await ProbeDownAsync(descend, "X0 Y0", ct).ConfigureAwait(false);
            ZSteps = 0;
            Changed?.Invoke();
            RaiseLog("Referencia tomada en X0 Y0; Z0 ajustado al contacto.");

            // 4) sondear cada punto en el orden del recorrido
            int done = 0;
            foreach (var p in map.ProbePoints)
            {
                ct.ThrowIfCancellationRequested();
                double travelMm = Math.Sqrt(
                    Math.Pow(Settings.MmFromSteps(Axis.X, p.XSteps - XSteps), 2) +
                    Math.Pow(Settings.MmFromSteps(Axis.Y, p.YSteps - YSteps), 2));
                long lift = travelMm >= 10.0 ? safeFar : safeNear;
                await RapidAsync(Position with { Z = lift }, ct).ConfigureAwait(false);
                await RapidAsync(Position with { X = p.XSteps, Y = p.YSteps }, ct).ConfigureAwait(false);

                string donde = $"X{Settings.MmFromSteps(Axis.X, p.XSteps):F2} Y{Settings.MmFromSteps(Axis.Y, p.YSteps):F2}";
                short ri = await ProbeDownAsync(descend, donde, ct).ConfigureAwait(false);

                // la posición reportada por el firmware resincroniza el contador de Z
                long z = (long)sign * (ri - r0);
                ZSteps = z;
                map.SetOffset(p.Cell, (int)z);
                done++;
                Changed?.Invoke();
                LevelingProgress?.Invoke(done, map.ProbePoints.Count, z * mmZ);
            }

            // 5) terminar en Z2 y el origen
            await RapidAsync(Position with { Z = lift2 }, ct).ConfigureAwait(false);
            await RapidAsync(Position with { X = 0, Y = 0 }, ct).ConfigureAwait(false);

            Leveling = map;
            ApplyLeveling = true;
            RaiseLog($"Calibración completa: {done} puntos; superficie entre {map.MinOffsetMm:+0.000;-0.000} y " +
                     $"{map.MaxOffsetMm:+0.000;-0.000} mm respecto a X0 Y0. Se aplicará al trabajo.");
            if (!string.IsNullOrEmpty(Program.FilePath))
            {
                try
                {
                    LevelingMapFile.Save(map, LevelingMapFile.PathFor(Program.FilePath));
                    RaiseLog($"Mapa guardado en {Path.GetFileName(LevelingMapFile.PathFor(Program.FilePath))}.");
                }
                catch (Exception ex) { RaiseLog("No se pudo guardar el mapa: " + ex.Message); }
            }
        }
        catch (ProbeAbortException ex) { RaiseWarning(ex.Message); }
        catch (OperationCanceledException) { RaiseWarning("Calibración cancelada."); }
        catch (MachineTimeoutException) { RaiseWarning("La máquina no envió la señal durante la calibración; revisa la conexión e intenta de nuevo."); }
        finally
        {
            if (Status == MachineStatus.Calibrando)
                SetStatus(IsConnected ? MachineStatus.Inactivo : MachineStatus.SinConexion);
            ReleaseActivity();
            Changed?.Invoke();
        }
    }

    /// <summary>Rápido con 0xFF durante la calibración; si la máquina lo interrumpe, aborta.</summary>
    private async Task RapidAsync(StepPosition target, CancellationToken ct)
    {
        var reply = await PlannedMoveAsync(target, PacketEncoder.MoveHeader, ct).ConfigureAwait(false);
        if (reply == null) return;
        SetSpindleFlag(false);
        throw new ProbeAbortException(reply.Value.Kind switch
        {
            MachineNotification.Released => "Máquina liberada con el botón físico; calibración cancelada.",
            MachineNotification.SpindleOverload => "Husillo sobrecargado; calibración cancelada.",
            _ => $"Calibración interrumpida por la máquina (respuesta {(byte)reply.Value.Kind}).",
        });
    }

    /// <summary>Baja Z con 0x3F hasta el toque y devuelve la Z en pasos reportada por el firmware.</summary>
    private async Task<short> ProbeDownAsync(long descendSteps, string donde, CancellationToken ct)
    {
        var reply = await PlannedMoveAsync(Position with { Z = Position.Z - descendSteps }, PacketEncoder.ProbeHeader, ct).ConfigureAwait(false);
        if (reply == null)
            throw new ProbeAbortException($"La sonda no detectó la placa en {donde} (bajó 10 mm sin contacto). " +
                                          "Acerca Z a la superficie y revisa la pinza.");
        if (reply.Value.Kind == MachineNotification.ProbeTouch)
            return reply.Value.ProbeZ;
        SetSpindleFlag(false);
        throw new ProbeAbortException(reply.Value.Kind == MachineNotification.Released
            ? "Máquina liberada con el botón físico; calibración cancelada."
            : "Husillo sobrecargado; calibración cancelada.");
    }

    // ---- paro de emergencia ----

    public void EmergencyStop()
    {
        RaiseWarning("PARO DE EMERGENCIA");
        StopAllJog();
        var old = _estopCts;
        _estopCts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
        Job?.Discard();
        if (Program != null)
            Job = new JobRunner(this, Program);
        SetSpindleFlag(false);
        _ = Task.Run(async () =>
        {
            // el semáforo lo liberan los bucles cancelados; aquí solo apagamos el husillo
            await Task.Delay(100).ConfigureAwait(false);
            try
            {
                if (_link != null)
                {
                    var cmd = new StepCommand(0, 0, 0, false);
                    await _link.SendAsync(PacketEncoder.EncodeMove(PacketEncoder.ProbeHeader, cmd, cmd, cmd), _estopCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            if (IsConnected) SetStatus(MachineStatus.Detenido);
        });
    }

    public void Dispose()
    {
        Disconnect();
        _keepAlive.Dispose();
        _estopCts.Dispose();
        _activity.Dispose();
    }
}
