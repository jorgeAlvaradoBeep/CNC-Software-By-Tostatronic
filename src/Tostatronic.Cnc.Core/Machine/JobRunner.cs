using System.Diagnostics;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Leveling;
using Tostatronic.Cnc.Core.Motion;
using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Core.Machine;

internal sealed class JobInterruptedException : Exception
{
    public JobInterruptedException(string message) : base(message) { }
}

/// <summary>
/// Ejecuta un programa de G-code contra la máquina: convierte cada línea en micro-comandos,
/// los agrupa en paquetes de 3 con cabecera 0xFF, respeta pausa/paro/reanudar (con regreso a
/// la posición pausada si la máquina se movió), M0/M6 (cambio de herramienta), M47, G4,
/// husillo M3/M5 y reporta progreso. Un timeout o una interrupción de la máquina dejan el
/// trabajo en pausa/detenido pero reanudable.
/// </summary>
public sealed class JobRunner
{
    private readonly MachineController _m;
    private readonly GCodeProgram _program;
    private readonly GCodeStream _stream;
    private readonly StepPlanner _planner = new();
    private readonly List<StepCommand> _buffer = new(3);
    private readonly Stopwatch _elapsed = new();

    private StepPosition _gen;           // posición NOMINAL generada (sin compensación de altura)
    private double _feed = 170.0;        // F modal, como Wegstr
    private string _lastCommand = "";
    private bool _programSpindle;        // lo que el G-code quiere del husillo (M3/M5)

    // compensación de altura: extras de Z por comando en el buffer y total ya confirmado,
    // para poder convertir entre posición nominal y posición de máquina (contadores)
    private LevelingCompensator? _lev;
    private readonly List<int> _bufferExtras = new(3);
    private long _ackedExtraZ;

    private volatile bool _pauseRequested;
    private volatile bool _stopRequested;
    private volatile bool _discarded;
    private TaskCompletionSource? _resumeSignal;
    private Task? _task;
    private bool _holdsActivity;

    public JobRunner(MachineController m, GCodeProgram program)
    {
        _m = m;
        _program = program;
        _stream = new GCodeStream(program);
    }

    public GCodeProgram Program => _program;
    public bool IsStarted => _task != null;
    public bool IsRunning => _task is { IsCompleted: false };
    public bool IsFinished { get; private set; }

    public int CurrentSourceLine { get; private set; }
    public string CurrentLineText { get; private set; } = "";
    public double ProgressPercent =>
        _stream.ParsedCount == 0 ? 0 : Math.Min(100.0, 100.0 * _stream.ParsedPosition / _stream.ParsedCount);
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public string? WaitMessage { get; private set; }

    internal void Start()
    {
        // el que llama ya adquirió la actividad del controlador
        _holdsActivity = true;
        _gen = _m.Position;
        _lev = _m.CreateCompensatorForJob();
        _task = Task.Run(() => RunAsync(_m.EstopToken));
    }

    public void RequestPause()
    {
        if (IsRunning) _pauseRequested = true;
    }

    public void RequestStop()
    {
        if (!IsStarted || IsFinished) return;
        if (_m.Status is MachineStatus.Pausado or MachineStatus.EsperandoHerramienta)
        {
            // ya está quieto: solo escalar a detenido y apagar el husillo
            _stopRequested = true;
            _m.SetSpindleFlag(false);
            _ = _m.SetSpindleAsync(false);
            _m.SetStatus(MachineStatus.Detenido);
            return;
        }
        _stopRequested = true;
    }

    public void Resume() => _resumeSignal?.TrySetResult();

    internal void Discard()
    {
        _discarded = true;
        _resumeSignal?.TrySetCanceled();
    }

    private bool SpindleFlag() => _m.SpindleOn;

    private async Task RunAsync(CancellationToken estop)
    {
        _m.SetStatus(MachineStatus.Ejecutando);
        _elapsed.Start();
        try
        {
            if (_lev != null)
                _m.RaiseLog($"Calibración de altura aplicada al trabajo ({_lev.PointCount} puntos).");
            if (_m.Settings.AutoSpindle)
            {
                _m.SetSpindleFlag(true);
                _programSpindle = true;
                await SendSpindlePacketAsync(estop).ConfigureAwait(false);
            }

            while (true)
            {
                estop.ThrowIfCancellationRequested();
                if (_discarded) throw new OperationCanceledException();
                if (_pauseRequested || _stopRequested)
                    await PausePointAsync(estop).ConfigureAwait(false);

                var line = _stream.Next();
                if (line == null)
                    break;

                CurrentSourceLine = line.SourceIndex + 1;
                if (line.Raw.Length > 0) CurrentLineText = line.Raw;
                _m.RaiseChanged();

                bool keepGoing = await ExecuteLineAsync(line, estop).ConfigureAwait(false);
                if (!keepGoing)
                    break;
            }

            await FlushAsync(estop).ConfigureAwait(false);
            _elapsed.Stop();
            _m.SetSpindleFlag(false);
            await SendSpindlePacketAsync(estop).ConfigureAwait(false);
            IsFinished = true;
            _m.SetStatus(MachineStatus.Terminado);
            _m.RaiseLog("Trabajo terminado.");
        }
        catch (OperationCanceledException)
        {
            _elapsed.Stop();
            if (!_discarded)
                _m.SetStatus(MachineStatus.Detenido);
        }
        catch (Exception ex)
        {
            _elapsed.Stop();
            _m.SetStatus(MachineStatus.Detenido);
            _m.RaiseWarning("Error en el trabajo: " + ex.Message);
        }
        finally
        {
            if (_holdsActivity)
            {
                _holdsActivity = false;
                _m.ReleaseActivity();
            }
        }
    }

    /// <summary>Devuelve false cuando el programa termina (M2/M30).</summary>
    private async Task<bool> ExecuteLineAsync(GCodeLine line, CancellationToken estop)
    {
        if (line.F is double f)
            _feed = Math.Min(f, SpeedModel.MaxFeedMmMin);

        switch (line.Command)
        {
            case "G00":
            case "G01":
            {
                var target = TargetOf(line);
                Func<double> advance;
                if (line.Command == "G00")
                {
                    long dx = target.X - _gen.X, dy = target.Y - _gen.Y, dz = target.Z - _gen.Z;
                    double dist = Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
                    advance = () => SpeedModel.RapidAdvance(dist, dx, dy, dz, _m.SpeedPercent);
                }
                else
                {
                    advance = () => SpeedModel.CuttingAdvance(_feed, _m.SpeedPercent);
                }
                await MoveToAsync(target, advance, cutting: line.Command == "G01", estop).ConfigureAwait(false);
                break;
            }
            case "G02":
            case "G03":
            {
                double x0 = _m.Settings.MmFromSteps(Axis.X, _gen.X);
                double y0 = _m.Settings.MmFromSteps(Axis.Y, _gen.Y);
                double z0 = _m.Settings.MmFromSteps(Axis.Z, _gen.Z);
                double x1 = line.X ?? x0, y1 = line.Y ?? y0, z1 = line.Z ?? z0;
                var points = ArcTessellator
                    .Tessellate(x0, y0, x1, y1, line.I!.Value, line.J!.Value, line.Command == "G02")
                    .ToList();
                Func<double> advance = () => SpeedModel.CuttingAdvance(_feed, _m.SpeedPercent);
                for (int k = 0; k < points.Count; k++)
                {
                    double zk = z0 + (z1 - z0) * (k + 1) / points.Count;
                    var target = new StepPosition(
                        _m.Settings.StepsFromMm(Axis.X, points[k].X),
                        _m.Settings.StepsFromMm(Axis.Y, points[k].Y),
                        _m.Settings.StepsFromMm(Axis.Z, zk));
                    await MoveToAsync(target, advance, cutting: true, estop).ConfigureAwait(false);
                }
                break;
            }
            case "G04":
            {
                await FlushAsync(estop).ConfigureAwait(false);
                double seconds = line.P ?? 0;
                if (seconds > 0)
                {
                    _m.RaiseLog($"Pausa G4 de {seconds:F1} s.");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), estop).ConfigureAwait(false);
                }
                break;
            }
            case "M03":
            case "M04":
                await FlushAsync(estop).ConfigureAwait(false);
                _m.SetSpindleFlag(true);
                _programSpindle = true;
                await SendSpindlePacketAsync(estop).ConfigureAwait(false);
                break;
            case "M05":
                await FlushAsync(estop).ConfigureAwait(false);
                _m.SetSpindleFlag(false);
                _programSpindle = false;
                await SendSpindlePacketAsync(estop).ConfigureAwait(false);
                break;
            case "M00":
            {
                await FlushAsync(estop).ConfigureAwait(false);
                string msg = _lastCommand == "M06"
                    ? "Cambia la herramienta y presiona INICIO para continuar."
                    : "Paro del programa (M0). Presiona INICIO para continuar.";
                await PausePointAsync(estop, toolMessage: msg).ConfigureAwait(false);
                break;
            }
            case "M06":
                break; // el aviso lo da el M0 que sigue, como en Wegstr
            case "M47":
                await FlushAsync(estop).ConfigureAwait(false);
                _stream.Rewind();
                _m.RaiseLog("M47: el programa vuelve a empezar.");
                break;
            case "M02":
            case "M30":
                return false;
            default:
                if (line.Command.Length > 0)
                    _m.RaiseLog($"Código no soportado, se ignora: {line.Command} (línea {line.SourceIndex + 1}).");
                break;
        }

        if (line.Command.Length > 0)
            _lastCommand = line.Command;
        return true;
    }

    private StepPosition TargetOf(GCodeLine line) => new(
        line.X is double x ? _m.Settings.StepsFromMm(Axis.X, x) : _gen.X,
        line.Y is double y ? _m.Settings.StepsFromMm(Axis.Y, y) : _gen.Y,
        line.Z is double z ? _m.Settings.StepsFromMm(Axis.Z, z) : _gen.Z);

    /// <summary>
    /// Mueve hasta el objetivo con pausa entre paquetes; si la máquina no responde o se
    /// interrumpe (botón/sobrecarga), queda en pausa/detenido y al reanudar replanifica
    /// desde la posición real. En movimientos de corte aplica la compensación de altura:
    /// ±1 paso por comando en cortes XY, completa en bajadas puras de Z (plunge).
    /// </summary>
    private async Task MoveToAsync(StepPosition target, Func<double> advance, bool cutting, CancellationToken estop)
    {
        while (true)
        {
            try
            {
                bool plunge = target.X == _gen.X && target.Y == _gen.Y;
                foreach (var cmd in _planner.Linear(_gen, target, advance, SpindleFlag))
                {
                    if (_pauseRequested || _stopRequested)
                    {
                        await PausePointAsync(estop).ConfigureAwait(false);
                        // tras regresar a la posición pausada se sigue con el mismo tramo
                    }
                    _gen = new StepPosition(_gen.X + cmd.Dx, _gen.Y + cmd.Dy, _gen.Z + cmd.Dz);
                    var send = cmd;
                    int extra = 0;
                    if (cutting && _lev != null)
                    {
                        extra = _lev.Extra(_gen.X, _gen.Y, cmd.Dz, rampStep: !plunge);
                        if (extra != 0)
                            send = cmd with { Dz = cmd.Dz + extra };
                    }
                    await AddCommandAsync(send, extra, estop).ConfigureAwait(false);
                }
                return;
            }
            catch (MachineTimeoutException)
            {
                DropBuffer();
                _m.RaiseWarning("La máquina no envió la señal; el trabajo queda en pausa. Revisa la conexión y reanuda.");
                _gen = NominalFromMachine(_m.Position);
                await PausePointAsync(estop, flush: false).ConfigureAwait(false);
            }
            catch (JobInterruptedException ex)
            {
                DropBuffer();
                _m.RaiseWarning(ex.Message);
                _gen = NominalFromMachine(_m.Position);
                _stopRequested = true;
                await PausePointAsync(estop, flush: false).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Posición nominal (del programa) que corresponde a una posición de máquina.</summary>
    private StepPosition NominalFromMachine(StepPosition machine) =>
        machine with { Z = machine.Z - _ackedExtraZ };

    /// <summary>Descarta los comandos no confirmados y regresa sus extras de Z a pendientes.</summary>
    private void DropBuffer()
    {
        if (_lev != null)
        {
            long sum = 0;
            foreach (int e in _bufferExtras) sum += e;
            _lev.ReturnUnsent(sum);
        }
        _buffer.Clear();
        _bufferExtras.Clear();
    }

    private async Task AddCommandAsync(StepCommand cmd, int extraZ, CancellationToken estop)
    {
        _buffer.Add(cmd);
        _bufferExtras.Add(extraZ);
        if (_buffer.Count == 3)
            await SendBufferAsync(estop).ConfigureAwait(false);
    }

    private async Task FlushAsync(CancellationToken estop)
    {
        if (_buffer.Count == 0) return;
        while (_buffer.Count < 3)
        {
            _buffer.Add(new StepCommand(0, 0, 0, SpindleFlag()));
            _bufferExtras.Add(0);
        }
        await SendBufferAsync(estop).ConfigureAwait(false);
    }

    private async Task SendBufferAsync(CancellationToken estop)
    {
        var reply = await _m.SendMoveInternalAsync(PacketEncoder.MoveHeader, _buffer, estop).ConfigureAwait(false);
        _buffer.Clear();
        if (reply.Kind is MachineNotification.Ack or MachineNotification.ProbeTouch)
        {
            foreach (int e in _bufferExtras) _ackedExtraZ += e;
            _bufferExtras.Clear();
        }
        else
        {
            // la máquina no ejecutó el paquete: los extras vuelven a pendientes
            DropBuffer();
        }
        if (reply.Kind == MachineNotification.Released)
            throw new JobInterruptedException("Máquina liberada con el botón físico; trabajo detenido.");
        if (reply.Kind == MachineNotification.SpindleOverload)
            throw new JobInterruptedException("Husillo sobrecargado; trabajo detenido.");
    }

    private async Task SendSpindlePacketAsync(CancellationToken estop)
    {
        var cmd = new StepCommand(0, 0, 0, _m.SpindleOn);
        var reply = await _m.SendMoveInternalAsync(PacketEncoder.MoveHeader, new[] { cmd, cmd, cmd }, estop).ConfigureAwait(false);
        if (reply.Kind == MachineNotification.Released)
            throw new JobInterruptedException("Máquina liberada con el botón físico.");
        if (reply.Kind == MachineNotification.SpindleOverload)
            throw new JobInterruptedException("Husillo sobrecargado.");
    }

    /// <summary>
    /// Punto de pausa: vacía el buffer, apaga el husillo si es paro, suelta la actividad para
    /// permitir jog/GO ZERO, espera Reanudar y, si la máquina se movió, regresa a la posición.
    /// </summary>
    private async Task PausePointAsync(CancellationToken estop, bool flush = true, string? toolMessage = null)
    {
        if (flush)
            await FlushAsync(estop).ConfigureAwait(false);

        bool asStop = _stopRequested;
        _pauseRequested = false;
        _stopRequested = false;
        var pausedAt = _m.Position;
        _elapsed.Stop();
        WaitMessage = toolMessage;

        if (asStop)
        {
            _m.SetSpindleFlag(false);
            try { await SendSpindlePacketAsync(estop).ConfigureAwait(false); }
            catch (MachineTimeoutException) { }
            catch (JobInterruptedException) { }
            _m.SetStatus(MachineStatus.Detenido);
        }
        else
        {
            _m.SetStatus(toolMessage != null ? MachineStatus.EsperandoHerramienta : MachineStatus.Pausado);
        }
        if (toolMessage != null)
            _m.RaiseWarning(toolMessage);
        _m.RaiseChanged();

        _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // permitir jog y GO ZERO durante la pausa
        _holdsActivity = false;
        _m.ReleaseActivity();
        try
        {
            await _resumeSignal.Task.WaitAsync(estop).ConfigureAwait(false);
        }
        finally
        {
            _resumeSignal = null;
            WaitMessage = null;
        }
        await _m.AcquireActivityAsync(estop).ConfigureAwait(false);
        _holdsActivity = true;

        if (_discarded) throw new OperationCanceledException();

        // si la máquina se movió durante la pausa, regresar (XY primero, luego Z)
        if (_m.Position != pausedAt)
        {
            _m.SetStatus(MachineStatus.Regresando);
            var xy = pausedAt with { Z = _m.Position.Z };
            var r1 = await _m.PlannedMoveAsync(xy, PacketEncoder.MoveHeader, estop).ConfigureAwait(false);
            if (r1 == null)
                await _m.PlannedMoveAsync(pausedAt, PacketEncoder.MoveHeader, estop).ConfigureAwait(false);
            _gen = NominalFromMachine(_m.Position);
            _planner.Reset();
        }

        if (_programSpindle && !_m.SpindleOn)
        {
            _m.SetSpindleFlag(true);
            await SendSpindlePacketAsync(estop).ConfigureAwait(false);
        }

        _m.SetStatus(MachineStatus.Ejecutando);
        _elapsed.Start();
    }
}
