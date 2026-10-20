using System.IO.Compression;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Geometry;
using Tostatronic.Cnc.Cam.Gerber;
using Tostatronic.Cnc.Cam.Jobs;
using Tostatronic.Cnc.Core.GCode;

namespace Tostatronic.Cnc.Tests;

public class GerberParserTests
{
    private static double AreaMm2(Paths64 paths) => Clipper.Area(paths) / (Geo.Scale * Geo.Scale);

    [Fact]
    public void TrazoYFlashBasicos()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,1.000000*%
            %ADD11R,2.000000X1.000000*%
            G01*
            %LPD*%
            D10*
            X100000Y100000D02*
            X200000Y100000D01*
            D11*
            X300000Y200000D03*
            M02*
            """);
        // trazo de 10 mm con ancho 1 (≈ 10.78 mm²) + rectángulo 2×1 aparte
        Assert.Equal(12.78, AreaMm2(img.Filled), 1);
        Assert.Single(img.Segments);
        var (minX, minY, maxX, maxY) = Geo.BoundsMm(img.Filled);
        Assert.Equal(9.5, minX, 2);
        Assert.Equal(31.0, maxX, 2);
        Assert.Equal(9.5, minY, 2);
        Assert.Equal(20.5, maxY, 2);
    }

    [Fact]
    public void FlashConAgujeroYObround()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %ADD12C,2.000000X0.800000*%
            %ADD13O,3.000000X1.000000*%
            D12*
            X0Y0D03*
            D13*
            X100000Y0D03*
            M02*
            """);
        // círculo d2 con agujero d0.8 (π(1−0.16)=2.64) + óvalo 3×1 (2·1 + π·0.25 = 2.785)
        Assert.Equal(2.64 + 2.78, AreaMm2(img.Filled), 1);
    }

    [Fact]
    public void RegionG36()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            G36*
            X0Y0D02*
            G01X100000Y0D01*
            Y100000D01*
            X0D01*
            Y0D01*
            G37*
            M02*
            """);
        Assert.Equal(100.0, AreaMm2(img.Filled), 2);
    }

    [Fact]
    public void PolaridadClaraResta()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,2.000000*%
            G36*
            X0Y0D02*
            X100000Y0D01*
            Y100000D01*
            X0D01*
            Y0D01*
            G37*
            %LPC*%
            D10*
            X50000Y50000D03*
            M02*
            """);
        // cuadrado de 100 menos círculo d2 (π)
        Assert.Equal(100 - Math.PI, AreaMm2(img.Filled), 1);
    }

    [Fact]
    public void ArcoCompletoG75()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,1.000000*%
            G75*
            D10*
            X0Y50000D02*
            G03X0Y50000I50000J0D01*
            M02*
            """);
        // anillo r=5 trazo 1 mm: π(5.5²−4.5²) = 10π ≈ 31.4
        Assert.Equal(31.4, AreaMm2(img.Filled), 0);
    }

    [Fact]
    public void MacroDonaConVariables()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %AMDONUT*
            1,1,$1,0,0*
            1,0,$2,0,0*%
            %ADD20DONUT,3X1*%
            D20*
            X0Y0D03*
            M02*
            """);
        Assert.Equal(Math.PI * (1.5 * 1.5 - 0.5 * 0.5), AreaMm2(img.Filled), 1);
    }

    [Fact]
    public void MacroLineaCentradaRotada()
    {
        var img = GerberParser.Parse("""
            %FSLAX34Y34*%
            %MOMM*%
            %AMROT*
            21,1,2,1,0,0,45*%
            %ADD21ROT*%
            D21*
            X0Y0D03*
            M02*
            """);
        Assert.Equal(2.0, AreaMm2(img.Filled), 2); // la rotación conserva el área
        Assert.True(Geo.InsideFilled(Geo.Pt(0, 0), img.Filled));
    }

    [Fact]
    public void CoordenadasEnPulgadas()
    {
        var img = GerberParser.Parse("""
            %FSLAX24Y24*%
            %MOIN*%
            %ADD10C,0.040000*%
            D10*
            X10000Y0D02*
            X20000Y0D01*
            M02*
            """);
        // 1" → 2" con ancho 0.04" = 25.4 mm de largo, 1.016 de ancho
        var (minX, _, maxX, _) = Geo.BoundsMm(img.Filled);
        Assert.Equal(25.4 - 0.508, minX, 1);
        Assert.Equal(50.8 + 0.508, maxX, 1);
    }
}

public class ExcellonParserTests
{
    [Fact]
    public void FormatoMetricoConDecimales()
    {
        var holes = ExcellonParser.Parse("""
            M48
            METRIC,LZ
            T01C0.800
            T02C1.000
            %
            T01
            X10.0Y10.0
            X12.5Y10.0
            T02
            X20.0Y20.0
            M30
            """);
        Assert.Equal(3, holes.Count);
        Assert.Equal(0.8, holes[0].Diameter, 3);
        Assert.Equal(12.5, holes[1].X, 3);
        Assert.Equal(1.0, holes[2].Diameter, 3);
    }

    [Fact]
    public void FormatoEnterosImplicitos()
    {
        var holes = ExcellonParser.Parse("""
            M48
            METRIC,LZ
            T01C1.000
            %
            T01
            X012500Y010000
            M30
            """);
        Assert.Single(holes);
        Assert.Equal(12.5, holes[0].X, 3);
        Assert.Equal(10.0, holes[0].Y, 3);
    }

    [Fact]
    public void FormatoEnPulgadas()
    {
        var holes = ExcellonParser.Parse("""
            M48
            INCH,LZ
            T01C0.0394
            %
            T01
            X1.0Y2.0
            M30
            """);
        Assert.Single(holes);
        Assert.Equal(25.4, holes[0].X, 3);
        Assert.Equal(50.8, holes[0].Y, 3);
        Assert.Equal(1.0, holes[0].Diameter, 2);
    }
}

public class CamGenerationTests
{
    private const string OutlineGko = """
        %FSLAX34Y34*%
        %MOMM*%
        %ADD10C,0.100000*%
        D10*
        X0Y0D02*
        X400000Y0D01*
        X400000Y300000D01*
        X0Y300000D01*
        X0Y0D01*
        M02*
        """;

    // dos redes: pad+pista (izquierda) y pad suelto (derecha arriba)
    private const string BottomGbl = """
        %FSLAX34Y34*%
        %MOMM*%
        %ADD10C,1.500000*%
        %ADD11C,0.800000*%
        D10*
        X50000Y50000D03*
        X350000Y250000D03*
        D11*
        X50000Y50000D02*
        X200000Y50000D01*
        M02*
        """;

    private const string DrillFile = """
        M48
        METRIC,LZ
        T01C0.900
        T02C1.000
        T03C3.200
        %
        T01
        X5.0Y5.0
        T02
        X35.0Y25.0
        T03
        X20.0Y15.0
        M30
        """;

    private static string MakeZip(params (string Name, string Content)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"cam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Gerber_PlacaPrueba.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return path;
    }

    private static BoardModel LoadBoard()
    {
        string zip = MakeZip(
            ("Gerber_BottomLayer.GBL", BottomGbl),
            ("Gerber_BoardOutlineLayer.GKO", OutlineGko),
            ("Drill_PTH_Through.DRL", DrillFile));
        try { return BoardLoader.LoadZip(zip); }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void CargaElZipYEncadenaElContorno()
    {
        var board = LoadBoard();
        Assert.StartsWith("PlacaPrueba", board.Name);
        Assert.NotNull(board.CopperBottom);
        Assert.Null(board.CopperTop);
        Assert.Equal(40.0, board.WidthMm, 2);
        Assert.Equal(30.0, board.HeightMm, 2);
        Assert.Single(board.Outline);
        Assert.Equal(3, board.Drills.Count);
    }

    [Fact]
    public void LaCaraInferiorSaleEnEspejo()
    {
        var board = LoadBoard();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
        // el barreno del pad izquierdo (5,5) queda en X = 40−5 = 35
        var all = r.DrillGroups.Values.SelectMany(g => g).ToList();
        Assert.Contains(all, p => Math.Abs(p.x - 35) < 0.01 && Math.Abs(p.y - 5) < 0.01);
        Assert.Contains(all, p => Math.Abs(p.x - 5) < 0.01 && Math.Abs(p.y - 25) < 0.01);
    }

    [Fact]
    public void ElAislamientoNoInvadeElCobre()
    {
        var board = LoadBoard();
        var p = new CamPreset();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, p);

        Assert.True(r.Isolation.Count >= 4, $"Solo {r.Isolation.Count} anillos."); // 2 redes × 2 pasadas
        // ningún vértice de aislamiento puede quedar a menos de tool/2 del cobre
        long minX = Geo.L(board.MinX), minY = Geo.L(board.MinY), maxX = Geo.L(board.MaxX);
        var copper = new Paths64();
        foreach (var path in r.Copper)
        {
            var p64 = new Path64(path.Count);
            foreach (var pt in path) p64.Add(Geo.Pt(pt.x, pt.y));
            copper.Add(p64);
        }
        var forbidden = Geo.Offset(Geo.Union(copper), p.ToolMm / 2 - 0.015);
        foreach (var ring in r.Isolation)
            foreach (var pt in ring)
                Assert.False(Geo.InsideFilled(Geo.Pt(pt.x, pt.y), forbidden),
                    $"El punto ({pt.x:F3}, {pt.y:F3}) invade el cobre.");
    }

    [Fact]
    public void AgrupaLasBrocasComoElScript()
    {
        Assert.Equal(0.8, CamGenerator.BitFor(0.9));
        Assert.Equal(1.0, CamGenerator.BitFor(1.0));
        Assert.Equal(1.0, CamGenerator.BitFor(1.1));
        Assert.Equal(1.2, CamGenerator.BitFor(1.2));
        Assert.Equal(1.2, CamGenerator.BitFor(3.2));

        var board = LoadBoard();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
        Assert.Equal(new[] { 0.8, 1.0, 1.2 }, r.DrillGroups.Keys.ToArray());

        var single = CamGenerator.Generate(board, BoardFace.Bottom,
            new CamPreset { DrillSingleBit = true, DrillSingleBitMm = 1.0 });
        Assert.Single(single.DrillGroups);
        Assert.Equal(3, single.DrillGroups[1.0].Count);
    }

    [Fact]
    public void AvisaCuandoLasRedesEstanMuyJuntas()
    {
        // dos pads d1.5 con 0.1 mm de separación: menor que el ancho efectivo 0.2
        string gbl = """
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,1.500000*%
            D10*
            X50000Y50000D03*
            X66000Y50000D03*
            M02*
            """;
        string zip = MakeZip(("Gerber_BottomLayer.GBL", gbl), ("Gerber_BoardOutlineLayer.GKO", OutlineGko));
        try
        {
            var board = BoardLoader.LoadZip(zip);
            var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
            Assert.Contains(r.Warnings, w => w.Contains("más cercanas"));
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void ElGcodeEmitidoTieneElFormatoDeLosScripts()
    {
        var board = LoadBoard();
        var p = new CamPreset();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, p);

        Assert.Equal(5, r.Files.Count); // aislamiento + 3 brocas + contorno
        Assert.Equal("PlacaPrueba_1_aislamiento.nc", r.Files[0].FileName);
        Assert.Equal("PlacaPrueba_2_barrenos_0_8mm.nc", r.Files[1].FileName);
        Assert.Equal("PlacaPrueba_3_contorno.nc", r.Files[^1].FileName);

        string iso = r.Files[0].Content;
        Assert.Contains("( PlacaPrueba - Aislamiento )", iso);
        Assert.Contains("( mm, GRBL. X0 Y0 = esquina inf-izq vista desde el cobre. Z0 = cobre )", iso);
        Assert.Contains("V-bit ancho efectivo 0.2 mm, prof 0.1 mm, 2 pasadas cada 0.15 mm, 150 mm/min", iso);
        Assert.Contains("G21 G90 G17 G94", iso);
        Assert.Contains("G0 Z15.000", iso);
        Assert.Contains("M3 S10000", iso);
        Assert.Contains("G4 P3", iso);
        Assert.Contains("G1 Z-0.100 F50", iso);
        Assert.Contains("F150", iso);
        Assert.EndsWith("M2" + Environment.NewLine, iso);

        string drill = r.Files[1].Content;
        Assert.Contains("( 1 barrenos a -1.8 mm )", drill);
        Assert.Contains("G1 Z-1.800 F30", drill);
        Assert.Contains("G0 Z0.500", drill);

        string contour = r.Files[^1].Content;
        Assert.Contains("( Fresa 1.0 mm por fuera, pasadas de 0.3 mm, sin pestanas )", contour);
        Assert.Contains("F100", contour);
        Assert.Contains("G1 Z-1.800 F50", contour); // última pasada a espesor + 0.2
    }

    [Fact]
    public void ElGcodeEmitidoLoAceptaNuestroParser()
    {
        var board = LoadBoard();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
        foreach (var (name, content) in r.Files)
        {
            var program = GCodeProgram.FromText(content, name);
            Assert.True(program.Lines.Count > 5, $"{name} quedó casi vacío.");
            // dentro de la placa más el contorno (0.5 mm afuera) y el regreso a X0 Y0
            Assert.InRange(program.MinX, -1.1, 0.1);
            Assert.InRange(program.MaxX, 0, board.WidthMm + 1.1);
            Assert.InRange(program.MinY, -1.1, 0.1);
            Assert.InRange(program.MaxY, 0, board.HeightMm + 1.1);
        }
        var isolation = GCodeProgram.FromText(r.Files[0].Content, "iso");
        Assert.Equal(-0.1, isolation.MinZ, 3);
        var contour = GCodeProgram.FromText(r.Files[^1].Content, "cont");
        Assert.Equal(-1.8, contour.MinZ, 3);
    }

    [Fact]
    public void LaPlacaDemoDelRepoGeneraTodoElFlujo()
    {
        var board = BoardLoader.LoadZip(Fixtures.GerberExample("Gerber_Demo_DIP8.zip"));
        Assert.Equal(40.0, board.WidthMm, 1);
        Assert.Equal(30.0, board.HeightMm, 1);
        Assert.Equal(12, board.Drills.Count);

        var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
        Assert.DoesNotContain(r.Warnings, w => w.Contains("más cercanas"));
        Assert.True(r.Isolation.Count >= 8, $"Solo {r.Isolation.Count} anillos."); // 4 redes × 2 pasadas
        Assert.Equal(2, r.DrillGroups.Count); // 0.8 y 1.0
        Assert.Equal(10, r.DrillGroups[0.8].Count);
        Assert.Equal(2, r.DrillGroups[1.0].Count);
        foreach (var (name, content) in r.Files)
            Assert.True(GCodeProgram.FromText(content, name).Lines.Count > 5);
    }

    [Fact]
    public void SinContornoUsaElRectanguloDelCobre()
    {
        string zip = MakeZip(("Gerber_BottomLayer.GBL", BottomGbl));
        try
        {
            var board = BoardLoader.LoadZip(zip);
            Assert.Contains(board.Warnings, w => w.Contains("contorno"));
            Assert.Single(board.Outline);
            var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
            Assert.NotEmpty(r.Contour);
        }
        finally { File.Delete(zip); }
    }
}
