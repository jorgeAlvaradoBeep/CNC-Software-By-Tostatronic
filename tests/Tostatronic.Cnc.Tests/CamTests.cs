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
    public void LZRellenaCerosALaDerechaYTZNo()
    {
        // LZ: los ceros a la izquierda están, pueden faltar los de la derecha
        var lz = ExcellonParser.Parse("""
            M48
            METRIC,LZ,000.000
            T01C1.000
            %
            T01
            X02025Y01
            M30
            """);
        Assert.Equal(20.25, lz[0].X, 3);
        Assert.Equal(10.0, lz[0].Y, 3);

        // TZ: los ceros a la derecha están, pueden faltar los de la izquierda (KiCad)
        var tz = ExcellonParser.Parse("""
            M48
            METRIC,TZ
            T01C1.000
            %
            T01
            X20251Y846
            M30
            """);
        Assert.Equal(20.251, tz[0].X, 3);
        Assert.Equal(0.846, tz[0].Y, 3);
    }

    [Fact]
    public void ComentarioFileFormatDefineLosDigitos()
    {
        var holes = ExcellonParser.Parse("""
            M48
            METRIC,LZ
            ;FILE_FORMAT=4:2
            T01C1.000
            %
            T01
            X000838Y001562
            M30
            """);
        Assert.Equal(8.38, holes[0].X, 3);
        Assert.Equal(15.62, holes[0].Y, 3);
    }

    [Fact]
    public void SlotCortoEsUnBarrenoEnElCentroYLargoSonDos()
    {
        var warnings = new List<string>();
        var holes = ExcellonParser.Parse("""
            M48
            METRIC,LZ,000.000
            T01C1.016
            %
            T01
            X031112Y060071G85X031118Y060071
            X010000Y010000G85X015000Y010000
            M30
            """, warnings);
        Assert.Equal(3, holes.Count);
        Assert.Equal(31.115, holes[0].X, 3); // pad ovalado de EasyEDA: slot de 0.006 mm → centro
        Assert.Equal(60.071, holes[0].Y, 3);
        Assert.Equal(10.0, holes[1].X, 3);   // slot de 5 mm → un barreno en cada extremo
        Assert.Equal(15.0, holes[2].X, 3);
        Assert.Single(warnings);
        Assert.Contains("más largo", warnings[0]);
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

    // ---- ZIP con la estructura exacta que exporta EasyEDA Std v6.5 (validada contra una
    //      exportación real el 2026-10-20): comentarios G04, FSLAX45Y45, líneas en blanco,
    //      %LPD*%, regiones G36 + %LPC*%, máscaras/serigrafía/capa interna que hay que ignorar,
    //      Excellon METRIC,LZ,000.000 con ;FILE_FORMAT, G05/G90, pads ovalados como slots G85
    //      de 0.006 mm y archivo aparte de vías. Geometría propia (placa 20 × 15 mm en 5.588, 50.8).

    private const string EasyHeader = """
        G04 Layer: {0}*
        G04 EasyEDA v6.5.22, 2023-01-21 22:26:58*
        G04 Gerber Generator version 0.2*
        G04 Scale: 100 percent, Rotated: No, Reflected: No *
        G04 Dimensions in millimeters *
        G04 leading zeros omitted , absolute positions ,4 integer and 5 decimal *
        %FSLAX45Y45*%
        %MOMM*%

        """;

    private static string Easy(string layer, string body) => string.Format(EasyHeader, layer) + body;

    private const string EasyGko = """
        %ADD10C,0.2540*%
        D10*
        X558800Y5080000D02*
        G01*
        X2558800Y5080000D01*
        X2558800Y6580000D01*
        X558800Y6580000D01*
        X558800Y5080000D01*

        %LPD*%
        M02*
        """;

    private const string EasyGbl = """
        %ADD10C,0.5000*%
        %ADD11O,1.7500092X1.1999976*%
        %ADD12R,1.5240X1.5240*%
        %ADD13C,1.8000*%

        %LPD*%
        D10*
        X800000Y5500000D02*
        G01*
        X1800000Y5500000D01*
        D11*
        X800000Y5500000D03*
        D12*
        X1800000Y5500000D03*
        D13*
        X2200000Y6000000D03*
        M02*
        """;

    private const string EasyGtl = """
        %ADD10C,1.0000*%
        %ADD11C,1.8000*%

        %LPD*%
        G36*
        X1000000Y5800000D02*
        G01*
        X1400000Y5800000D01*
        X1400000Y6200000D01*
        X1000000Y6200000D01*
        X1000000Y5800000D01*
        G37*
        %LPC*%
        D10*
        X1200000Y6000000D03*
        %LPD*%
        D11*
        X2200000Y6000000D03*
        M02*
        """;

    private const string EasyGts = """
        %ADD10C,9.0000*%
        D10*
        X1500000Y5800000D03*
        M02*
        """;

    private const string EasyDrl = """
        M48
        METRIC,LZ,000.000
        ;FILE_FORMAT=3:3
        ;TYPE=PLATED
        ;Layer: PTH_Through
        ;EasyEDA v6.5.22, 2023-01-21 22:26:58
        ;Gerber Generator version 0.2
        ;Holesize 1 = 1.016 mm
        T01C1.016
        ;Holesize 2 = 1.200 mm
        T02C1.200
        %
        G05
        G90
        T01
        X008000Y055000G85X008006Y055000
        X018000Y055000
        T02
        X022000Y060000
        M30
        """;

    private const string EasyDrlVia = """
        M48
        METRIC,LZ,000.000
        ;FILE_FORMAT=3:3
        ;TYPE=PLATED
        ;Layer: PTH_Through_Via
        ;Holesize 1 = 0.306 mm
        T01C0.306
        %
        G05
        G90
        T01
        X012000Y060000
        M30
        """;

    [Fact]
    public void CargaUnZipConLaEstructuraDeEasyEdaStd()
    {
        string zip = MakeZip(
            ("Gerber_BoardOutlineLayer.GKO", Easy("BoardOutlineLayer", EasyGko)),
            ("Gerber_BottomLayer.GBL", Easy("BottomLayer", EasyGbl)),
            ("Gerber_TopLayer.GTL", Easy("TopLayer", EasyGtl)),
            ("Gerber_TopSolderMaskLayer.GTS", Easy("TopSolderMaskLayer", EasyGts)),
            ("Gerber_BottomSolderMaskLayer.GBS", Easy("BottomSolderMaskLayer", EasyGts)),
            ("Gerber_TopSilkscreenLayer.GTO", Easy("TopSilkscreenLayer", EasyGts)),
            ("Gerber_TopPasteMaskLayer.GTP", Easy("TopPasteMaskLayer", EasyGts)),
            ("Gerber_InnerLayer21.G21", Easy("InnerLayer21", "M02*\n")),
            ("Drill_PTH_Through.DRL", EasyDrl),
            ("Drill_PTH_Through_Via.DRL", EasyDrlVia));
        try
        {
            var board = BoardLoader.LoadZip(zip);
            Assert.Empty(board.Warnings);
            Assert.Equal(20.0, board.WidthMm, 3);
            Assert.Equal(15.0, board.HeightMm, 3);
            Assert.Equal(5.588, board.MinX, 3);
            Assert.Equal(50.8, board.MinY, 3);
            Assert.Single(board.Outline);
            Assert.True(board.HasFace(BoardFace.Bottom));
            Assert.True(board.HasFace(BoardFace.Top));

            // 3 PTH (el slot de 0.006 mm es UN barreno en su centro) + 1 vía
            Assert.Equal(4, board.Drills.Count);
            var slotHole = board.Drills.Single(d => Math.Abs(d.Diameter - 1.016) < 1e-6 && d.X < 10);
            Assert.Equal(8.003, slotHole.X, 3);
            Assert.Equal(55.0, slotHole.Y, 3);
            Assert.Equal(1.2, board.Drills.Single(d => d.X > 21).Diameter, 3);
            Assert.Equal(0.306, board.Drills.Single(d => Math.Abs(d.X - 12) < 1e-6).Diameter, 3);

            // la máscara (apertura de 9 mm) no contaminó el cobre: el cobre inferior cabe en la placa
            var (cx0, cy0, cx1, cy1) = Geo.BoundsMm(board.CopperBottom!);
            Assert.True(cx0 > 5.588 && cx1 < 25.588 && cy0 > 50.8 && cy1 < 65.8, "El cobre se sale de la placa.");

            // la región G36 con el hueco LPC: el centro (12,60) está vacío, (10.5,58.5) está lleno
            Assert.False(Geo.InsideFilled(Geo.Pt(12.0, 60.0), board.CopperTop!));
            Assert.True(Geo.InsideFilled(Geo.Pt(10.5, 58.5), board.CopperTop!));

            foreach (var face in new[] { BoardFace.Bottom, BoardFace.Top })
            {
                var r = CamGenerator.Generate(board, face, new CamPreset());
                Assert.Empty(r.Warnings);
                Assert.Equal(4, r.DrillCount);
                Assert.Equal(3, r.DrillGroups.Count); // 0.8 (vía), 1.0 (1.016), 1.2
                Assert.Equal(5, r.Files.Count);
                foreach (var (name, content) in r.Files)
                    Assert.True(GCodeProgram.FromText(content, name).Lines.Count > 5, name);
            }

            // la vía en diseño (12, 60) → cara inferior en espejo: X = 25.588 − 12, Y = 60 − 50.8
            var bottom = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
            var via = bottom.DrillGroups[0.8].Single();
            Assert.Equal(13.588, via.x, 3);
            Assert.Equal(9.2, via.y, 3);
        }
        finally { File.Delete(zip); }
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
