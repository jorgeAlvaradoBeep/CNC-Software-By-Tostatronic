namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Codifica y decodifica los paquetes del protocolo binario Wegstr (ver docs/wegstr-analisis.md §2.3).
///
/// Paquete de movimiento: 7 bytes = cabecera + 3 comandos de 2 bytes.
///   Byte 1: [ z2 z1 z0 | sx | x3 x2 x1 x0 ]   Byte 2: [ sp | sz | z8 | sy | y3 y2 y1 y0 ]
/// Paquete de ajuste: 2 bytes = 0x7F + rendimiento (2..20).
/// </summary>
public static class PacketEncoder
{
    /// <summary>Movimiento sin detección de toque de sonda (ejecución de trabajos).</summary>
    public const byte MoveHeader = 0xFF;

    /// <summary>Movimiento con detección de toque (jog, sondeo, detección de máquina).</summary>
    public const byte ProbeHeader = 0x3F;

    /// <summary>Paquete de ajuste [0x7F, rendimiento]; también sirve de keep-alive.</summary>
    public const byte SettingsHeader = 0x7F;

    public const int CommandsPerPacket = 3;
    public const int MaxStepsPerCommand = 14;
    public const int MovePacketLength = 7;

    public static byte[] EncodeMove(byte header, StepCommand c0, StepCommand c1, StepCommand c2)
    {
        var packet = new byte[MovePacketLength];
        packet[0] = header;
        EncodeCommand(c0, packet, 1);
        EncodeCommand(c1, packet, 3);
        EncodeCommand(c2, packet, 5);
        return packet;
    }

    public static byte[] EncodeMove(byte header, IReadOnlyList<StepCommand> commands)
    {
        if (commands.Count != CommandsPerPacket)
            throw new ArgumentException($"Un paquete lleva exactamente {CommandsPerPacket} comandos.", nameof(commands));
        return EncodeMove(header, commands[0], commands[1], commands[2]);
    }

    public static byte[] EncodeSettings(int performance)
    {
        if (performance is < 2 or > 20)
            throw new ArgumentOutOfRangeException(nameof(performance), "El rendimiento va de 2 a 20.");
        return new[] { SettingsHeader, (byte)performance };
    }

    private static void EncodeCommand(StepCommand c, byte[] buffer, int offset)
    {
        int ax = Math.Abs(c.Dx), ay = Math.Abs(c.Dy), az = Math.Abs(c.Dz);
        if (ax > MaxStepsPerCommand || ay > MaxStepsPerCommand || az > MaxStepsPerCommand)
            throw new ArgumentOutOfRangeException(nameof(c), $"Comando fuera de rango (máx ±{MaxStepsPerCommand} pasos): {c}");

        int b1 = ax + (c.Dx < 0 ? 16 : 0);
        int b2 = ay + (c.Dy < 0 ? 16 : 0) + (c.Dz < 0 ? 64 : 0) + (c.Spindle ? 128 : 0);
        if (az >= 8)
        {
            b1 += (az - 8) << 5;
            b2 += 32;
        }
        else
        {
            b1 += az << 5;
        }
        buffer[offset] = (byte)b1;
        buffer[offset + 1] = (byte)b2;
    }

    public static StepCommand[] DecodeMove(byte[] packet)
    {
        if (packet.Length != MovePacketLength)
            throw new ArgumentException("Paquete de movimiento inválido.", nameof(packet));
        var result = new StepCommand[CommandsPerPacket];
        for (int i = 0; i < CommandsPerPacket; i++)
            result[i] = DecodeCommand(packet[1 + 2 * i], packet[2 + 2 * i]);
        return result;
    }

    private static StepCommand DecodeCommand(byte b1, byte b2)
    {
        int x = b1 & 0x0F;
        if ((b1 & 0x10) != 0) x = -x;
        int y = b2 & 0x0F;
        if ((b2 & 0x10) != 0) y = -y;
        int z = (b1 >> 5) & 0x07;
        if ((b2 & 0x20) != 0) z += 8;
        if ((b2 & 0x40) != 0) z = -z;
        bool spindle = (b2 & 0x80) != 0;
        return new StepCommand(x, y, z, spindle);
    }
}
