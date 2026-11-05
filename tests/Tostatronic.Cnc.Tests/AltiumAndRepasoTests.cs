using System.IO.Compression;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Geometry;
using Tostatronic.Cnc.Cam.Gerber;
using Tostatronic.Cnc.Cam.Jobs;
using Tostatronic.Cnc.Core.GCode;

namespace Tostatronic.Cnc.Tests;

/// <summary>
/// Dialecto de Altium Designer (validado el 2026-11-04 con una exportación real): pulgadas,
/// FSTAX23Y23 (ceros FINALES omitidos), G54Dnn, D01/D03 modales, taladro como Gerber
/// (PTH_Drill.gbr), archivos Pads_*/Legend_* que hay que ignorar y reportes .apr/.REP/.RUL.
/// </summary>
public class AltiumGerberTests
{
    // dos redes: pad A (1.000", 1.000") con pista hasta (1.000", 1.200") y pad B (1.100", 1.000");
    // más una pista suelta a 3.000" (50 mm de todo lo demás): error típico de un diseño de alumno
    private const string TopGbr = """
        %FSTAX23Y23*%
        %MOIN*%
        %SFA1B1*%

        %IPPOS*%
        %ADD10C,0.062000*%
        %ADD11C,0.025000*%
        G54D10*
        X01Y01D03*
        X011*
        G54D11*
        X01Y01D02*
        Y012D01*
        X03Y01D02*
        Y0105D01*
        M02*
        """;

    private const string BotGbr = """
        %FSTAX23Y23*%
        %MOIN*%
        %SFA1B1*%

        %IPPOS*%
        %ADD10C,0.062000*%
        G54D10*
        X01Y01D03*
        X011D03*
        M02*
        """;

    private const string DrillGbr = """
        %FSTAX23Y23*%
        %MOIN*%
        %SFA1B1*%

        %IPPOS*%
        %ADD31C,0.035000*%
        G54D31*
        X01Y01D03*
        X011D03*
        M02*
        """;

    // la serigrafía dibuja un rectángulo grande: si se tomara como cobre o contorno, cambiaría todo
    private const string LegendGbr = """
        %FSTAX23Y23*%
        %MOIN*%
        %ADD14C,0.010000*%
        G54D14*
        X005Y005D02*
        X02D01*
        Y02*
        X005*
        Y005*
        M02*
        """;

    private static string MakeZip(params (string Name, string Content)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"altium_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "PCB_Proyecto-de-prueba_2026-11-03-macro.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return path;
    }

    private static BoardModel LoadAltium(BoardLoadOptions? o = null)
    {
        string zip = MakeZip(
            ("PCB_Proyecto-de-prueba_Copper_Signal_Top.gbr", TopGbr),
            ("PCB_Proyecto-de-prueba_Copper_Signal_Bot.gbr", BotGbr),
            ("PCB_Proyecto-de-prueba_Pads_Top.gbr", TopGbr),
            ("PCB_Proyecto-de-prueba_Pads_Bot.gbr", BotGbr),
            ("PCB_Proyecto-de-prueba_Legend_Top.gbr", LegendGbr),
            ("PCB_Proyecto-de-prueba_Legend_Bot.gbr", "%FSLAX25Y25*%\n%MOIN*%\nM02*\n"),
            ("PCB_Proyecto-de-prueba_PTH_Drill.gbr", DrillGbr),
            ("PCB_Proyecto-de-prueba.apr", "D10   ROUND 62.000 62.000 0.000 FLASH 0.000\n"),
            ("PCB_Proyecto-de-prueba.REP", "Generating : Top Layer\n"),
            ("PCB_Proyecto-de-prueba.RUL", "RuleKind=Width|Minimum=10.00\n"),
            ("PCB_Proyecto-de-prueba.EXTREP", ".gbr Top Layer\n"));
        try { return BoardLoader.LoadZip(zip, o); }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void CerosFinalesOmitidosEnPulgadasYCodigosDModales()
    {
        var img = GerberParser.Parse(TopGbr);
        // "X01" = 01.000", "X011" = 01.100", "Y012" = 01.200"
        Assert.Equal(2, img.Flashes.Count);           // el segundo flash no trae D03: es modal
        Assert.Equal(25.4, img.Flashes[0].X, 3);
        Assert.Equal(27.94, img.Flashes[1].X, 3);
        Assert.Equal(1.5748, img.Flashes[0].W, 3);   // 0.062"
        Assert.Equal(2, img.Segments.Count);          // "Y012D01" y la pista suelta
        Assert.Equal(30.48, img.Segments[0].B.y, 3);  // 1.2"
        Assert.True(img.HasTraces);
        Assert.Empty(img.Warnings);
    }

    [Fact]
    public void ClasificaLosArchivosDeAltium()
    {
        static BoardLoader.FileKind K(string n) => BoardLoader.Classify(n, () => "");
        Assert.Equal(BoardLoader.FileKind.CopperTop, K("PCB_x_Copper_Signal_Top.gbr"));
        Assert.Equal(BoardLoader.FileKind.CopperBottom, K("PCB_x_Copper_Signal_Bot.gbr"));
        Assert.Equal(BoardLoader.FileKind.GerberDrill, K("PCB_x_PTH_Drill.gbr"));
        Assert.Equal(BoardLoader.FileKind.GerberDrill, K("PCB_x_NPTH_Drill.gbr"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("PCB_x_Pads_Top.gbr"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("PCB_x_Legend_Top.gbr"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("PCB_x.apr"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("PCB_x.REP"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("PCB_x_Drill_Drawing.gbr"));
        Assert.Equal(BoardLoader.FileKind.Outline, K("PCB_x_Mechanical_1.gbr"));
        Assert.Equal(BoardLoader.FileKind.Outline, K("PCB_x.GKO"));
        // EasyEDA y KiCad siguen igual
        Assert.Equal(BoardLoader.FileKind.CopperBottom, K("Gerber_BottomLayer.GBL"));
        Assert.Equal(BoardLoader.FileKind.Outline, K("Gerber_BoardOutlineLayer.GKO"));
        Assert.Equal(BoardLoader.FileKind.Excellon, K("Drill_PTH_Through.DRL"));
        Assert.Equal(BoardLoader.FileKind.Skip, K("Gerber_TopSolderMaskLayer.GTS"));
        Assert.Equal(BoardLoader.FileKind.CopperTop, K("placa-F_Cu.gbr"));
        Assert.Equal(BoardLoader.FileKind.Outline, K("placa-Edge_Cuts.gbr"));
        // Excellon .txt se reconoce por el contenido
        Assert.Equal(BoardLoader.FileKind.Excellon, BoardLoader.Classify("placa.txt", () => "M48\nMETRIC\nT01C0.8\n%\n"));
        Assert.Equal(BoardLoader.FileKind.Skip, BoardLoader.Classify("notas.txt", () => "hola"));
    }

    [Fact]
    public void ReconoceUnaCapaConPistasArribaYTaladroEnGerber()
    {
        var board = LoadAltium();
        Assert.Equal("Proyecto-de-prueba", board.Name); // sin PCB_, sin fecha ni -macro
        Assert.Equal("Altium Designer", board.Source);
        Assert.True(board.TopHasTraces);
        Assert.False(board.BottomHasTraces);
        Assert.True(board.IsSingleLayer);
        Assert.Equal(BoardFace.Top, board.PreferredFace);
        Assert.Contains(board.Info, i => i.Contains("UNA capa") && i.Contains("SUPERIOR"));

        Assert.Equal(2, board.Drills.Count);
        Assert.All(board.Drills, d => Assert.Equal(0.889, d.Diameter, 3)); // 0.035"
        Assert.Equal(25.4, board.Drills[0].X, 3);
        Assert.Equal(27.94, board.Drills[1].X, 3);
    }

    [Fact]
    public void DescartaElCobreSueltoYAvisa()
    {
        var board = LoadAltium();
        Assert.Single(board.Stray);
        Assert.Equal(76.2, board.Stray[0].CenterX, 1);
        Assert.True(board.Stray[0].DistanceMm > 40);
        Assert.Contains(board.Warnings, w => w.Contains("FUERA de la placa") && w.Contains("descartó"));
        // sin el suelto, la placa es el cobre (0.969"…1.131" × 0.969"…1.2125") más 2 mm por lado
        Assert.True(board.OutlineSynthetic);
        Assert.Equal(0.162 * 25.4 + 4, board.WidthMm, 2);
        Assert.Equal(0.2435 * 25.4 + 4, board.HeightMm, 2);
        Assert.Contains(board.Warnings, w => w.Contains("contorno") && w.Contains("2 mm"));

        var kept = LoadAltium(new BoardLoadOptions { DropStrayCopper = false });
        Assert.True(kept.WidthMm > 50, "Conservando el suelto, la placa debe salir ancha.");
        Assert.Contains(kept.Warnings, w => w.Contains("Se conserva"));
    }

    [Fact]
    public void ElMargenSinContornoEsConfigurable()
    {
        var board = LoadAltium(new BoardLoadOptions { OutlineMarginMm = 5 });
        Assert.Equal(0.162 * 25.4 + 10, board.WidthMm, 2);
    }

    [Fact]
    public void LaCaraSuperiorSaleSinEspejoYGiraSiNoCabe()
    {
        var board = LoadAltium();
        double w = board.WidthMm, h = board.HeightMm; // 8.11 × 10.18
        var p = new CamPreset();

        var plano = CamGenerator.Generate(board, BoardFace.Top, p, new CamOptions { TravelXMm = 100, TravelYMm = 140 });
        Assert.False(plano.Rotated);
        Assert.True(plano.FitsMachine);
        var holes = plano.DrillGroups[0.8];
        // pad A: x = 25.4 − (0.969"·25.4 − 2) = 2.787, y igual; pad B 2.54 más a la derecha
        Assert.Contains(holes, q => Math.Abs(q.x - 2.787) < 0.01 && Math.Abs(q.y - 2.787) < 0.01);
        Assert.Contains(holes, q => Math.Abs(q.x - 5.327) < 0.01 && Math.Abs(q.y - 2.787) < 0.01);
        Assert.DoesNotContain("_girada", plano.Files[0].FileName);
        Assert.Contains("_top_", plano.Files[0].FileName);

        // automático: con 9 mm de X no cabe sin girar (8.11 cabe, 10.18 no en Y=9) pero sí girada
        var auto = CamGenerator.Generate(board, BoardFace.Top, p, new CamOptions { TravelXMm = 12, TravelYMm = 9 });
        Assert.True(auto.Rotated);
        Assert.True(auto.FitsMachine);
        Assert.Equal(h, auto.WidthMm, 6);
        Assert.Equal(w, auto.HeightMm, 6);
        // giro horario: (x, y) → (y, W − x)
        var girados = auto.DrillGroups[0.8];
        Assert.Contains(girados, q => Math.Abs(q.x - 2.787) < 0.01 && Math.Abs(q.y - (w - 2.787)) < 0.01);
        Assert.Contains(girados, q => Math.Abs(q.x - 2.787) < 0.01 && Math.Abs(q.y - (w - 5.327)) < 0.01);
        Assert.Contains("_girada_", auto.Files[0].FileName);
        Assert.Contains("girada 90 grados", auto.Files[0].Content);
        Assert.Contains(auto.Info, i => i.Contains("girada 90"));
        // todo el recorrido cabe en el nuevo tamaño
        foreach (var ring in auto.Isolation)
            foreach (var q in ring)
                Assert.True(q.x >= -0.5 && q.x <= auto.WidthMm + 0.5 && q.y >= -0.5 && q.y <= auto.HeightMm + 0.5);

        // forzado: gira aunque quepa
        var forzado = CamGenerator.Generate(board, BoardFace.Top, p, new CamOptions { Rotation = RotationMode.Rotate90 });
        Assert.True(forzado.Rotated);
        // nunca: avisa que no cabe
        var nunca = CamGenerator.Generate(board, BoardFace.Top, p, new CamOptions { Rotation = RotationMode.None, TravelXMm = 12, TravelYMm = 9 });
        Assert.False(nunca.Rotated);
        Assert.False(nunca.FitsMachine);
        Assert.Contains(nunca.Warnings, w2 => w2.Contains("NO cabe") && w2.Contains("otra orientación"));
        // ni girada
        var nada = CamGenerator.Generate(board, BoardFace.Top, p, new CamOptions { TravelXMm = 5, TravelYMm = 5 });
        Assert.False(nada.FitsMachine);
        Assert.Contains(nada.Warnings, w2 => w2.Contains("ni girada"));
    }

    [Fact]
    public void AvisaSiSeEligeLaCaraQueSoloTraePads()
    {
        var board = LoadAltium();
        var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
        Assert.Contains(r.Warnings, w => w.Contains("solo trae pads"));
    }

    [Fact]
    public void UbicaLosPuentesEntreRedesMuyJuntas()
    {
        // dos pads de 1.1 mm a 1.25 mm de centro (0.15 de separación) y otro lejos
        const string gbl = """
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,1.100000*%
            D10*
            X100000Y100000D03*
            X112500Y100000D03*
            X300000Y300000D03*
            M02*
            """;
        string zip = MakeZip(("Gerber_BottomLayer.GBL", gbl));
        try
        {
            var board = BoardLoader.LoadZip(zip);
            var r = CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
            Assert.Contains(r.Warnings, w => w.Contains("más cercanas") && w.Contains("X"));
            // el pad lejano (a 26 mm) se descartó como cobre suelto; la placa son los dos pads más 2 mm de margen
            Assert.Single(board.Stray);
            Assert.Equal(1.25 + 1.1 + 4, board.WidthMm, 3);
            var b = Assert.Single(r.Bridges);
            // el hueco entre los pads queda justo al centro de la placa (en espejo da lo mismo)
            Assert.Equal(board.WidthMm / 2, b.x, 2);
            Assert.Equal(board.HeightMm / 2, b.y, 2);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void AvisaDeAnillosDelgadosYBarrenosFueraDelCobre()
    {
        const string gbl = """
            %FSLAX34Y34*%
            %MOMM*%
            %ADD10C,1.000000*%
            %ADD11C,2.000000*%
            D10*
            X100000Y100000D03*
            D11*
            X200000Y100000D03*
            M02*
            """;
        const string drl = """
            M48
            METRIC,LZ
            T01C0.900
            %
            T01
            X10.0Y10.0
            X20.0Y10.0
            X15.0Y15.0
            M30
            """;
        string zip = MakeZip(("Gerber_BottomLayer.GBL", gbl), ("Drill_PTH_Through.DRL", drl));
        try
        {
            var board = BoardLoader.LoadZip(zip);
            Assert.Contains(board.Warnings, w => w.Contains("anillo de cobre") && w.Contains("0.05"));
            Assert.Contains(board.Warnings, w => w.Contains("1 barreno(s) caen fuera"));
        }
        finally { File.Delete(zip); }
    }
}

public class RepasoTests
{
    private static CamResult Demo()
    {
        var board = BoardLoader.LoadZip(Fixtures.GerberExample("Gerber_Demo_DIP8.zip"));
        return CamGenerator.Generate(board, BoardFace.Bottom, new CamPreset());
    }

    [Fact]
    public void SeparaElAislamientoGeneradoEnSusAnillos()
    {
        var r = Demo();
        var (name, content) = r.Files[0];
        var prog = RepasoProgram.Parse(content, name);
        Assert.Equal(r.Isolation.Count, prog.Paths.Count);
        Assert.All(prog.Paths, p => Assert.True(p.IsClosed));
        Assert.Equal(0.1, prog.DepthMm, 6);
        Assert.Equal(2.0, prog.SafeZMm, 6);
        Assert.Equal(15.0, prog.TravelZMm, 6);
        Assert.Equal(150, prog.FeedMmMin);
        Assert.Equal(50, prog.PlungeMmMin);
        Assert.StartsWith("( Demo_DIP8 - Aislamiento )", prog.Header[0]);
        Assert.Contains(prog.Header, l => l.StartsWith("G21"));
        Assert.Contains(prog.Header, l => l.StartsWith("M3 S"));
        Assert.Equal("G0 Z15.000", prog.Footer[0]);
        Assert.Contains("M2", prog.Footer);
        // el primer anillo coincide punto a punto (más el cierre)
        Assert.Equal(r.Isolation[0].Count + 1, prog.Paths[0].Points.Count);
        Assert.Equal(r.Isolation[0][0].x, prog.Paths[0].Points[0].x, 3);
    }

    [Fact]
    public void LeeLosArchivosViejosDeLosScripts()
    {
        var prog = RepasoProgram.Load(Fixtures.Example("LM555_v2_1_aislamiento.nc"));
        Assert.True(prog.Paths.Count > 5);
        Assert.Equal(0.1, prog.DepthMm, 6);
        Assert.Equal(2.0, prog.SafeZMm, 6);
        Assert.True(prog.Header.Count >= 5);
        Assert.Contains(prog.Footer, l => l.StartsWith("M5"));
    }

    [Fact]
    public void ExportaSoloLoMarcadoConLaProfundidadPedida()
    {
        var r = Demo();
        var prog = RepasoProgram.Parse(r.Files[0].Content, r.Files[0].FileName);
        var ex = prog.Export(new[] { 0, 2 }, Array.Empty<RectMm>(), 0.15);
        Assert.Equal(2, ex.Pieces);
        Assert.Equal(2, ex.FullPaths);
        Assert.Contains("G1 Z-0.150 F50", ex.Content);
        Assert.DoesNotContain("Z-0.100", ex.Content);
        Assert.Contains("( Repaso: 2 trayectorias completas + 0 zonas = 2 tramos, a 0.15 mm )", ex.Content);
        Assert.StartsWith("( Demo_DIP8 - Aislamiento )", ex.Content);
        Assert.EndsWith("M2\r\n", ex.Content.Replace("\n", "\r\n").Replace("\r\r", "\r"));
        var parsed = GCodeProgram.FromText(ex.Content, "repaso");
        Assert.True(parsed.Lines.Count > 10);
        Assert.Equal(-0.15, parsed.MinZ, 6);
        // mismo largo que los dos anillos originales
        Assert.Equal(prog.Paths[0].LengthMm + prog.Paths[2].LengthMm, ex.LengthMm, 3);
        // y se vuelve a poder cargar en el repaso
        var again = RepasoProgram.Parse(ex.Content, "x.nc");
        Assert.Equal(2, again.Paths.Count);
        Assert.Equal(0.15, again.DepthMm, 6);
    }

    [Fact]
    public void LasZonasRecortanLasTrayectoriasNoMarcadas()
    {
        var square = new List<PointD> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) };
        // zona que cubre la mitad derecha: un solo tramo seguido (medio lado de abajo + lado derecho + medio lado de arriba)
        var runs = RepasoProgram.ClipPath(square, new RectMm(5, -1, 11, 11));
        Assert.Single(runs);
        double total = runs.Sum(run => run.Zip(run.Skip(1), (a, b) => double.Hypot(b.x - a.x, b.y - a.y)).Sum());
        Assert.Equal(20.0, total, 6); // 5 + 10 + 5
        // zona que no toca nada
        Assert.Empty(RepasoProgram.ClipPath(square, new RectMm(20, 20, 30, 30)));

        var r = Demo();
        var prog = RepasoProgram.Parse(r.Files[0].Content, r.Files[0].FileName);
        var b = prog.Paths[0].Bounds;
        var zone = new RectMm(b.X0 - 1, b.Y0 - 1, (b.X0 + b.X1) / 2, b.Y1 + 1);
        var ex = prog.Export(Array.Empty<int>(), new[] { zone }, 0.12);
        Assert.True(ex.Pieces >= 1);
        Assert.Equal(1, ex.Zones);
        Assert.True(ex.LengthMm < prog.TotalLengthMm);
        Assert.Throws<InvalidOperationException>(() => prog.Export(Array.Empty<int>(), Array.Empty<RectMm>(), 0.12));
    }

    [Fact]
    public void UnaSeccionEsSoloUnTramoDeLaTrayectoria()
    {
        var p = new RepasoPath();
        p.Points.AddRange(new PointD[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) }); // cuadrado cerrado, 40 mm
        Assert.True(p.IsClosed);
        Assert.Equal(4, p.MaxU);

        // posiciones a lo largo: mitad del lado de abajo y mitad del lado derecho
        var (ua, da) = p.Project(5, -0.3);
        Assert.Equal(0.5, ua, 9);
        Assert.Equal(0.3, da, 9);
        Assert.Equal(1.5, p.Project(10.2, 5).U, 9);
        var mid = p.At(1.5);
        Assert.Equal(10, mid.x, 9); Assert.Equal(5, mid.y, 9);

        // tramo hacia adelante: (5,0) -> (10,0) -> (10,5): 10 mm, 3 puntos
        var fwd = p.Slice(0.5, 1.5);
        Assert.Equal(3, fwd.Count);
        Assert.Equal(10.0, p.SliceLengthMm(0.5, 1.5), 9);
        // el otro recorrido (dando la vuelta por el cierre): 30 mm
        var back = p.Slice(1.5, 0.5);
        Assert.Equal(30.0, p.SliceLengthMm(1.5, 0.5), 9);
        Assert.Equal(new PointD(10, 5), back[0]);
        Assert.Equal(new PointD(5, 0), back[^1]);
        Assert.DoesNotContain(back.Skip(1).Take(back.Count - 2), q => q.x == 10 && q.y == 5);

        // sin muestras elige el más corto; con muestras, por donde pasó el ratón
        Assert.Equal((0.5, 1.5), p.ChooseArc(1.5, 0.5));
        Assert.Equal((1.5, 0.5), p.ChooseArc(0.5, 1.5, new[] { 2.5, 3.5, 3.9 }));
        Assert.True(RepasoPath.ContainsForward(3.5, 0.5, 0.1));
        Assert.False(RepasoPath.ContainsForward(3.5, 0.5, 2.0));

        // abierta: siempre del menor al mayor
        var open = new RepasoPath();
        open.Points.AddRange(new PointD[] { new(0, 0), new(10, 0), new(20, 0) });
        Assert.False(open.IsClosed);
        Assert.Equal((0.25, 1.75), open.ChooseArc(1.75, 0.25, new[] { 1.9 }));
        Assert.Equal(15.0, open.SliceLengthMm(1.75, 0.25), 9);
    }

    [Fact]
    public void ExportaLasSeccionesEnVezDeLaTrayectoriaCompleta()
    {
        var r = Demo();
        var prog = RepasoProgram.Parse(r.Files[0].Content, r.Files[0].FileName);
        var path = prog.Paths[1];
        double quarter = path.MaxU / 4;
        var sec = new RepasoSection(1, quarter, 2 * quarter);
        double secLen = path.SliceLengthMm(sec.From, sec.To);
        Assert.True(secLen > 0 && secLen < path.LengthMm);

        var ex = prog.Export(new[] { 0 }, Array.Empty<RectMm>(), new[] { sec }, 0.14);
        Assert.Equal(2, ex.Pieces);
        Assert.Equal(1, ex.FullPaths);
        Assert.Equal(1, ex.Sections);
        Assert.Equal(prog.Paths[0].LengthMm + secLen, ex.LengthMm, 3);
        Assert.Contains("( Repaso: 1 trayectorias completas + 1 secciones + 0 zonas = 2 tramos, a 0.14 mm )", ex.Content);
        Assert.Contains("G1 Z-0.140 F50", ex.Content);
        var again = RepasoProgram.Parse(ex.Content, "x.nc");
        Assert.Equal(2, again.Paths.Count);
        Assert.Contains(again.Paths, q => Math.Abs(q.LengthMm - secLen) < 1e-3 && !q.IsClosed);

        // una zona que cubre todo no vuelve a sacar la trayectoria con sección (ni la marcada completa)
        var b = prog.Bounds;
        var all = new RectMm(b.X0 - 1, b.Y0 - 1, b.X1 + 1, b.Y1 + 1);
        var ex2 = prog.Export(new[] { 0 }, new[] { all }, new[] { sec }, 0.12);
        Assert.Equal(prog.Paths.Count, ex2.Pieces); // 1 completa + 1 sección + las demás por la zona
        Assert.Equal(prog.TotalLengthMm - path.LengthMm + secLen, ex2.LengthMm, 2);

        // si la trayectoria está marcada completa, la sección no cuenta
        var ex3 = prog.Export(new[] { 1 }, Array.Empty<RectMm>(), new[] { sec }, 0.12);
        Assert.Equal(1, ex3.Pieces);
        Assert.Equal(0, ex3.Sections);
        Assert.Contains("( Repaso: 1 trayectorias completas + 0 zonas = 1 tramos", ex3.Content);
    }

    [Fact]
    public void EligeLaTrayectoriaMasCercanaAlClic()
    {
        var r = Demo();
        var prog = RepasoProgram.Parse(r.Files[0].Content, r.Files[0].FileName);
        var p0 = prog.Paths[0].Points[0];
        Assert.Equal(0, prog.Pick(p0.x, p0.y, 0.5));
        Assert.Equal(-1, prog.Pick(-100, -100, 0.5));
    }

    [Fact]
    public void LaSeleccionSeGuardaJuntoAlArchivo()
    {
        string nc = Path.Combine(Path.GetTempPath(), $"rep_{Guid.NewGuid():N}.nc");
        File.WriteAllText(nc, "x");
        try
        {
            new RepasoSelection
            {
                Selected = { 3, 1 }, Zones = { new[] { 1.0, 2.0, 3.0, 4.0 } },
                Sections = { new RepasoSection(5, 12.25, 40.5) }, DepthMm = 0.18, PathCount = 7,
            }.Save(nc);
            Assert.True(File.Exists(RepasoSelection.PathFor(nc)));
            var s = RepasoSelection.Load(nc, 7)!;
            Assert.Equal(new[] { 3, 1 }, s.Selected);
            Assert.Equal(0.18, s.DepthMm, 6);
            var z = Assert.Single(s.ZoneRects());
            Assert.Equal(new RectMm(1, 2, 3, 4), z);
            var sec = Assert.Single(s.Sections);
            Assert.Equal(5, sec.Path); Assert.Equal(12.25, sec.From, 9); Assert.Equal(40.5, sec.To, 9);
            Assert.Null(RepasoSelection.Load(nc, 8)); // otro número de trayectorias: no corresponde

            // un .repaso.json viejo (sin secciones) se sigue leyendo
            File.WriteAllText(RepasoSelection.PathFor(nc), "{ \"Selected\": [ 92 ], \"Zones\": [], \"DepthMm\": 0.12, \"PathCount\": 7 }");
            var old = RepasoSelection.Load(nc, 7)!;
            Assert.Equal(new[] { 92 }, old.Selected);
            Assert.Empty(old.Sections);
        }
        finally
        {
            File.Delete(nc);
            File.Delete(RepasoSelection.PathFor(nc));
        }
    }
}
