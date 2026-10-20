using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.Tests;

public class PacketEncoderTests
{
    [Fact]
    public void CodificaComandoSimple()
    {
        // (1, 2, 3, sin husillo): b1 = 1 + (3<<5) = 97, b2 = 2
        var p = PacketEncoder.EncodeMove(PacketEncoder.MoveHeader,
            new StepCommand(1, 2, 3, false), StepCommand.Zero, StepCommand.Zero);
        Assert.Equal(0xFF, p[0]);
        Assert.Equal(97, p[1]);
        Assert.Equal(2, p[2]);
        Assert.Equal(0, p[3]);
        Assert.Equal(0, p[4]);
    }

    [Fact]
    public void CodificaNegativosZGrandeYHusillo()
    {
        // (-14, -14, -14, husillo): b1 = 14+16+((14-8)<<5) = 222, b2 = 14+16+32+64+128 = 254
        var p = PacketEncoder.EncodeMove(PacketEncoder.ProbeHeader,
            new StepCommand(-14, -14, -14, true), StepCommand.Zero, StepCommand.Zero);
        Assert.Equal(0x3F, p[0]);
        Assert.Equal(222, p[1]);
        Assert.Equal(254, p[2]);
    }

    [Fact]
    public void RoundTripDeTodoElRango()
    {
        foreach (int x in new[] { -14, -8, -7, -1, 0, 1, 7, 8, 14 })
        foreach (int y in new[] { -14, -3, 0, 3, 14 })
        foreach (int z in new[] { -14, -8, -7, 0, 7, 8, 14 })
        foreach (bool sp in new[] { false, true })
        {
            var cmd = new StepCommand(x, y, z, sp);
            var packet = PacketEncoder.EncodeMove(PacketEncoder.MoveHeader, cmd, cmd, cmd);
            var decoded = PacketEncoder.DecodeMove(packet);
            Assert.All(decoded, d => Assert.Equal(cmd, d));
        }
    }

    [Fact]
    public void RechazaMasDe14Pasos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketEncoder.EncodeMove(PacketEncoder.MoveHeader,
                new StepCommand(15, 0, 0, false), StepCommand.Zero, StepCommand.Zero));
    }

    [Fact]
    public void PaqueteDeAjuste()
    {
        var p = PacketEncoder.EncodeSettings(4);
        Assert.Equal(new byte[] { 0x7F, 4 }, p);
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketEncoder.EncodeSettings(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketEncoder.EncodeSettings(21));
    }

    [Fact]
    public void PaqueteDeDeteccionEsNulo()
    {
        var p = PortScanner.DetectionPacket();
        Assert.Equal(PacketEncoder.ProbeHeader, p[0]);
        var decoded = PacketEncoder.DecodeMove(p);
        Assert.All(decoded, d => Assert.True(d.IsZeroMove && !d.Spindle));
    }
}
