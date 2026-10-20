using Tostatronic.Cnc.Core.GCode;

namespace Tostatronic.Cnc.Tests;

public class GCodeParserTests
{
    private static GCodeLine? Parse(params string[] lines)
    {
        var p = new GCodeParser();
        GCodeLine? last = null;
        for (int i = 0; i < lines.Length; i++)
            last = p.ParseLine(lines[i], i);
        return last;
    }

    [Fact]
    public void LineaDePreparacionNoEjecutaNada()
    {
        Assert.Null(Parse("G21 G90 G17 G94"));
        Assert.Null(Parse("( comentario )"));
        Assert.Null(Parse(""));
        Assert.Null(Parse("N110G71G91.1"));
    }

    [Fact]
    public void MovimientosBasicos()
    {
        var l = Parse("G0 Z15.000")!;
        Assert.Equal("G00", l.Command);
        Assert.Equal(15.0, l.Z);
        Assert.Null(l.X);

        l = Parse("G1 X3.077 Y4.945 F150")!;
        Assert.Equal("G01", l.Command);
        Assert.Equal(3.077, l.X);
        Assert.Equal(4.945, l.Y);
        Assert.Equal(150, l.F);
    }

    [Fact]
    public void HusilloYPausas()
    {
        var l = Parse("M3 S10000")!;
        Assert.Equal("M03", l.Command);
        Assert.Equal(10000, l.S);

        l = Parse("G4 P3")!;
        Assert.Equal("G04", l.Command);
        Assert.Equal(3, l.P);

        l = Parse("M5")!;
        Assert.Equal("M05", l.Command);

        l = Parse("M2")!;
        Assert.Equal("M02", l.Command);
    }

    [Fact]
    public void ArcoDelSampleOficial()
    {
        var l = Parse("N230G2X1.849Y-1.216I0.425J0.000")!;
        Assert.Equal("G02", l.Command);
        Assert.Equal(1.849, l.X);
        Assert.Equal(-1.216, l.Y);
        Assert.Equal(0.425, l.I);
        Assert.Equal(0.0, l.J);
        Assert.Equal(230, l.N);
    }

    [Fact]
    public void ArcoSinIJFalla()
    {
        Assert.Throws<GCodeException>(() => Parse("G2 X5 Y5"));
    }

    [Fact]
    public void PlanoNoSoportadoFalla()
    {
        Assert.Throws<GCodeException>(() => Parse("G18"));
    }

    [Fact]
    public void ComandoModalDeMovimiento()
    {
        // tras un G1, una línea con solo coordenadas sigue siendo G1
        var l = Parse("G1 Z-0.08 F170", "X1.5 Y2.5")!;
        Assert.Equal("G01", l.Command);
        Assert.Equal(1.5, l.X);

        // sin G previo, "X0 Y0 F170" del sample oficial es rápido
        l = Parse("X0.000Y0.000F170.0")!;
        Assert.Equal("G00", l.Command);
    }

    [Fact]
    public void LineaCombinadaDelPostprocesadorOficial()
    {
        // la primera G de la línea manda (G00) y el resto se ignora
        var l = Parse("N140G00G43Z2.000H1")!;
        Assert.Equal("G00", l.Command);
        Assert.Equal(2.0, l.Z);
    }

    [Fact]
    public void CicloDeTaladradoSeExpande()
    {
        var program = GCodeProgram.FromText("""
            G21 G90
            G0 Z2
            G81 X5 Y5 Z-1.8 R0.5 F30
            M2
            """);
        var stream = new GCodeStream(program);
        var seq = new List<GCodeLine>();
        GCodeLine? l;
        while ((l = stream.Next()) != null) seq.Add(l);

        // G0 Z2, luego la expansión: G0 XY, G0 Z(R), G1 Z-1.8 F30, G0 Z(retorno=2), M2
        Assert.Equal("G00", seq[0].Command);                    // Z2
        Assert.Equal("G00", seq[1].Command);                    // X5 Y5
        Assert.Equal(5.0, seq[1].X);
        Assert.Equal("G00", seq[2].Command);                    // Z0.5 (R)
        Assert.Equal(0.5, seq[2].Z);
        Assert.Equal("G01", seq[3].Command);                    // Z-1.8 F30
        Assert.Equal(-1.8, seq[3].Z);
        Assert.Equal(30, seq[3].F);
        Assert.Equal("G00", seq[4].Command);                    // de regreso a Z2
        Assert.Equal(2.0, seq[4].Z);
        Assert.Equal("M02", seq[5].Command);
    }

    [Theory]
    [InlineData("LM555_v2_1_aislamiento.nc", 0, 60, 0, 50, -0.2)]
    [InlineData("Receptor_AM_1_aislamiento.nc", 0, 120, 0, 120, -0.2)]
    [InlineData("Bascula_1_bottom_aislamiento.nc", 0, 90, 0, 60, -0.2)]
    [InlineData("Bascula_2_barrenos_1_0mm.nc", 0, 90, 0, 60, -2.0)]
    public void LosEjemplosRealesSeParseanCompletos(string file, double minX, double maxX, double minY, double maxY, double minZ)
    {
        var program = GCodeProgram.Load(Fixtures.Example(file));
        Assert.True(program.Lines.Count > 10, "muy pocas líneas parseadas");
        Assert.InRange(program.MinX, minX - 5, maxX);
        Assert.InRange(program.MaxX, minX, maxX);
        Assert.InRange(program.MinY, minY - 5, maxY);
        Assert.InRange(program.MaxY, minY, maxY);
        Assert.True(program.MinZ >= minZ - 0.01, $"MinZ={program.MinZ}");
    }
}
