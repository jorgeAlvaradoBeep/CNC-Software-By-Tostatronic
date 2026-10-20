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

    /// <summary>Superficie de la "placa" en mm absolutos del simulador: z = f(x, y). null = la sonda nunca toca.</summary>
    public Func<double, double, double>? SurfaceZMm { get; set; }

    public double MmPerStep => 10.0 / StepsPer10mm;

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
                    ZSteps += cmd.Dz;
                    if (probe && SurfaceZMm != null)
                    {
                        double surf = SurfaceZMm(XSteps * MmPerStep, YSteps * MmPerStep);
                        if (ZSteps * MmPerStep <= surf)
                        {
                            ZSteps = (long)Math.Round(surf / MmPerStep);
                            touched = true;
                            break;
                        }
                    }
                }
                if (touched)
                    Respond(4, (byte)(ZSteps >> 8), (byte)ZSteps);
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
