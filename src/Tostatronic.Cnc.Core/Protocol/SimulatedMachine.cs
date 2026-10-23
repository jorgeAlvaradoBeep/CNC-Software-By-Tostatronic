using System.Threading.Channels;

namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Firmware Wegstr falso para trabajar sin la CNC: ejecuta los paquetes a la cadencia del
/// firmware real, lleva su propia posición en pasos, contesta ACK, y simula la sonda de Z
/// contra una superficie configurable (toque → respuesta [4, hi, lo] y paro del paquete).
/// </summary>
public sealed class SimulatedMachine : ISerialLink
{
    private readonly Channel<byte[]> _rx = Channel.CreateUnbounded<byte[]>();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public string Name => "Simulador";
    public bool IsOpen { get; private set; }
    public event Action<byte[]>? DataReceived;

    /// <summary>Milisegundos por comando (cadencia inferida del firmware real ≈ 19.8 ms). 0 = instantáneo (pruebas).</summary>
    public double CommandPeriodMs { get; set; } = 19.8;

    public int StepsPer10mm { get; set; } = 2500;

    // Posición absoluta del simulador en pasos (independiente del cero de trabajo de la PC).
    public long XSteps, YSteps, ZSteps;
    public bool SpindleOn { get; private set; }
    public int Performance { get; private set; } = 4;

    /// <summary>
    /// Contador de Z del firmware tal como lo reporta en el toque (confirmado con la máquina
    /// real el 2026-10-21): NO es una posición absoluta, CRECE AL BAJAR y solo se mantiene
    /// coherente mientras la máquina se mueve con paquetes 0x3F; con 0xFF cada paso de Z lo
    /// decrementa sin importar el sentido. Arranca en un valor arbitrario.
    /// </summary>
    public long ProbeCounter { get; private set; } = 1000;

    /// <summary>Si es true la máquina deja de contestar (simula el cuelgue del USB visto en hardware).</summary>
    public bool Silent { get; set; }

    /// <summary>Superficie de la "placa" en mm absolutos del simulador: z = f(x, y). null = la sonda nunca toca.</summary>
    public Func<double, double, double>? SurfaceZMm { get; set; }

    public double MmPerStep => 10.0 / StepsPer10mm;

    private bool InContact()
    {
        if (SurfaceZMm == null) return false;
        return ZSteps * MmPerStep <= SurfaceZMm(XSteps * MmPerStep, YSteps * MmPerStep);
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _cts?.Cancel();
    }

    public void Write(byte[] data)
    {
        if (!IsOpen) throw new InvalidOperationException("Simulador no abierto.");
        _rx.Writer.TryWrite(data);
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _rx.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (Silent)
                    continue; // colgada: ni ejecuta ni contesta
                if (packet.Length == 2 && packet[0] == PacketEncoder.SettingsHeader)
                {
                    Performance = packet[1];
                    Respond(1);
                    continue;
                }
                if (packet.Length != PacketEncoder.MovePacketLength)
                    continue; // paquete desconocido: el firmware real lo ignoraría

                bool probe = packet[0] == PacketEncoder.ProbeHeader;
                bool touched = false;
                foreach (var cmd in PacketEncoder.DecodeMove(packet))
                {
                    if (CommandPeriodMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(CommandPeriodMs), ct).ConfigureAwait(false);
                    SpindleOn = cmd.Spindle;
                    XSteps += cmd.Dx;
                    YSteps += cmd.Dy;
                    long zBefore = ZSteps;
                    int dz = cmd.Dz;
                    // con la sonda cerrada el firmware descarta la bajada (en modo 0x3F)
                    if (probe && dz < 0 && InContact())
                        dz = 0;
                    ZSteps += dz;
                    if (probe && SurfaceZMm != null)
                    {
                        double surf = SurfaceZMm(XSteps * MmPerStep, YSteps * MmPerStep);
                        if (ZSteps * MmPerStep <= surf)
                        {
                            ZSteps = Math.Max(ZSteps, (long)Math.Round(surf / MmPerStep));
                            touched = true;
                        }
                    }
                    long executed = ZSteps - zBefore;
                    ProbeCounter += probe ? -executed : -Math.Abs(executed);
                    if (touched)
                        break; // el resto del paquete se descarta
                }
                if (touched)
                {
                    short v = (short)ProbeCounter;
                    Respond(4, (byte)(v >> 8), (byte)v);
                }
                else
                    Respond(1);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Respond(params byte[] data) => DataReceived?.Invoke(data);

    /// <summary>Simula el botón físico de la máquina (respuesta 3, "liberada").</summary>
    public void PressReleaseButton() => Respond(3);

    /// <summary>Simula una sobrecarga del husillo (respuesta 2).</summary>
    public void TriggerSpindleOverload() => Respond(2);

    public void Dispose() => Close();
}
