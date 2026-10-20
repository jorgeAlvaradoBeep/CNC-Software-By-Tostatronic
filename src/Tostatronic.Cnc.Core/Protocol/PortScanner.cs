namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Detección de la máquina: en cada puerto se manda un paquete 0x3F con 3 movimientos nulos;
/// si contesta 1, 2 o 3 en menos de 500 ms, ahí está la Wegstr.
/// </summary>
public static class PortScanner
{
    public static byte[] DetectionPacket() =>
        PacketEncoder.EncodeMove(PacketEncoder.ProbeHeader, StepCommand.Zero, StepCommand.Zero, StepCommand.Zero);

    /// <summary>
    /// Prueba un puerto ya abierto. Devuelve la respuesta si es una Wegstr, null si no contesta.
    /// </summary>
    public static async Task<MachineReply?> ProbePortAsync(ISerialLink link, int timeoutMs = 500, CancellationToken ct = default)
    {
        using var wl = new WegstrLink(link) { TimeoutMs = timeoutMs };
        try
        {
            var reply = await wl.SendAsync(DetectionPacket(), ct).ConfigureAwait(false);
            return reply;
        }
        catch (MachineTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recorre los puertos y devuelve el primero donde responde una Wegstr, o null.
    /// </summary>
    public static async Task<string?> FindMachineAsync(
        IEnumerable<string> portNames,
        Func<string, ISerialLink> linkFactory,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        foreach (var name in portNames)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"Probando puerto {name}...");
            ISerialLink? link = null;
            try
            {
                link = linkFactory(name);
                link.Open();
                var reply = await ProbePortAsync(link, 500, ct).ConfigureAwait(false);
                if (reply != null)
                {
                    log?.Invoke($"Wegstr encontrada en {name} (respuesta {(byte)reply.Value.Kind}).");
                    return name;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  {name}: {ex.Message}");
            }
            finally
            {
                link?.Dispose();
            }
        }
        log?.Invoke("No se encontró la máquina en ningún puerto.");
        return null;
    }
}
