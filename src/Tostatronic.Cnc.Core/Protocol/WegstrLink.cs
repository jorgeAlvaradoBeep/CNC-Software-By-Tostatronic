namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>Respuestas de la máquina (1 byte, salvo el toque de sonda que trae 2 bytes de dato).</summary>
public enum MachineNotification : byte
{
    Ack = 1,
    SpindleOverload = 2,
    Released = 3,
    ProbeTouch = 4,
}

/// <summary>Resultado de un envío: la primera respuesta recibida después del paquete.</summary>
public readonly record struct MachineReply(MachineNotification Kind, short ProbeZ)
{
    public bool IsAck => Kind == MachineNotification.Ack;
}

public sealed class MachineTimeoutException : Exception
{
    public MachineTimeoutException() : base("La máquina no envió la señal (timeout).") { }
}

/// <summary>
/// Capa de protocolo sobre el enlace serial: control de flujo stop-and-wait (un paquete en
/// vuelo, se espera la respuesta), timeout de 2 s y reensamblado del mensaje de toque de
/// sonda [0x04, hi, lo] que puede llegar partido en varias lecturas.
/// </summary>
public sealed class WegstrLink : IDisposable
{
    private readonly ISerialLink _link;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile TaskCompletionSource<MachineReply>? _pending;
    private int _rxState;
    private byte _touchHi;

    public int TimeoutMs { get; set; } = 2000;

    /// <summary>Toda respuesta recibida, solicitada o no (p. ej. botón físico de la máquina).</summary>
    public event Action<MachineReply>? NotificationReceived;

    public event Action<byte[]>? RawSent;
    public event Action<byte[]>? RawReceived;

    public ISerialLink Serial => _link;

    public WegstrLink(ISerialLink link)
    {
        _link = link;
        _link.DataReceived += OnData;
    }

    /// <summary>
    /// Manda un paquete y espera la primera respuesta de la máquina. Lanza
    /// <see cref="MachineTimeoutException"/> si no contesta en <see cref="TimeoutMs"/>.
    /// </summary>
    public async Task<MachineReply> SendAsync(byte[] packet, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<MachineReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            RawSent?.Invoke(packet);
            _link.Write(packet);
            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new MachineTimeoutException();
            }
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    private void OnData(byte[] data)
    {
        RawReceived?.Invoke(data);
        foreach (byte b in data)
            Feed(b);
    }

    private void Feed(byte b)
    {
        switch (_rxState)
        {
            case 1:
                _touchHi = b;
                _rxState = 2;
                return;
            case 2:
                _rxState = 0;
                Deliver(new MachineReply(MachineNotification.ProbeTouch, (short)((_touchHi << 8) | b)));
                return;
        }
        switch (b)
        {
            case 1: Deliver(new MachineReply(MachineNotification.Ack, 0)); break;
            case 2: Deliver(new MachineReply(MachineNotification.SpindleOverload, 0)); break;
            case 3: Deliver(new MachineReply(MachineNotification.Released, 0)); break;
            case 4: _rxState = 1; break;
            default: break; // byte desconocido: se ignora
        }
    }

    private void Deliver(MachineReply reply)
    {
        _pending?.TrySetResult(reply);
        NotificationReceived?.Invoke(reply);
    }

    public void Dispose()
    {
        _link.DataReceived -= OnData;
        _gate.Dispose();
    }
}
