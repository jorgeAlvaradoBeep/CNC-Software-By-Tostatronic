using System.Globalization;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Jobs;
using Tostatronic.Cnc.Core.Config;

// Línea de comandos del CAM, para convertir sin abrir la app (o desde un script):
//
//   tostatronic-cnc gerber <zip|carpeta> [--salida DIR] [--cara auto|inferior|superior]
//        [--girar auto|no|si] [--recorrido 100x140] [--margen 2] [--conservar-suelto]
//        [--preset "nombre"] [--tool 0.2] [--depth 0.1] [--passes 2] [--step 0.15] [--feed 150]
//        [--plunge 50] [--rpm 10000] [--thick 1.6] [--broca-unica 1.0]
//
//   tostatronic-cnc repaso <archivo.nc> [--anillos 1,5,9] [--zona x0,y0,x1,y1 ...]
//        [--tramo N:x0,y0:x1,y1 ...] [--depth 0.12] [--salida archivo.nc]
//
// --tramo: sección de la trayectoria N (numerada desde 1) entre el punto de la trayectoria más
// cercano a (x0,y0) y el más cercano a (x1,y1); en un anillo toma el recorrido más corto.
//
// Imprime el reporte de validación y escribe los .nc (más <placa>_reporte.txt) en la carpeta de salida
// (por defecto, junto al ZIP). Código de salida: 0 bien, 1 error, 2 la placa no cabe en la máquina.

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] is "-h" or "--help" or "ayuda")
{
    Console.WriteLine("""
        Uso:
          tostatronic-cnc gerber <zip|carpeta> [opciones]
              --salida DIR          carpeta para los .nc (default: junto al ZIP)
              --cara auto|inferior|superior   cara a fresar (auto = la que trae pistas)
              --girar auto|no|si    girar 90 grados (auto = solo si no cabe sin girar)
              --recorrido 100x140   recorrido de la máquina X x Y en mm (default: ajustes guardados)
              --margen 2            margen (mm) alrededor del cobre si no hay capa de contorno
              --conservar-suelto    no descartar el cobre suelto lejos de la placa
              --preset "nombre"     preset guardado; o parámetros sueltos:
              --tool --depth --passes --step --feed --plunge --rpm --thick --broca-unica
          tostatronic-cnc repaso <archivo.nc> [--anillos 1,5,9] [--zona x0,y0,x1,y1 ...]
                                [--tramo N:x0,y0:x1,y1 ...] [--depth 0.12] [--salida archivo.nc]
              --anillos             trayectorias completas (numeradas desde 1, como en la app)
              --zona                de las no marcadas, solo lo que cae dentro del rectangulo
              --tramo               seccion de la trayectoria N entre los puntos mas cercanos a (x0,y0) y (x1,y1)
        """);
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "gerber" => Gerber(args.Skip(1).ToArray()),
        "repaso" => Repaso(args.Skip(1).ToArray()),
        _ => Fail($"Comando desconocido: {args[0]} (usa gerber o repaso)."),
    };
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

int Fail(string msg)
{
    Console.Error.WriteLine("ERROR: " + msg);
    return 1;
}

string? Opt(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    return null;
}

bool Flag(string[] a, string name) => a.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

double? OptD(string[] a, string name) =>
    Opt(a, name) is { } s && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, inv, out double v) ? v : null;

int Gerber(string[] a)
{
    if (a.Length == 0 || a[0].StartsWith("--")) return Fail("Falta el ZIP o la carpeta de los Gerber.");
    string source = Path.GetFullPath(a[0]);
    if (!File.Exists(source) && !Directory.Exists(source)) return Fail($"No existe: {source}");

    var loadOptions = new BoardLoadOptions
    {
        DropStrayCopper = !Flag(a, "--conservar-suelto"),
        OutlineMarginMm = OptD(a, "--margen") ?? 2.0,
    };
    var board = BoardLoader.LoadPath(source, loadOptions);

    var face = (Opt(a, "--cara") ?? "auto").ToLowerInvariant() switch
    {
        "inferior" or "bottom" or "inf" => BoardFace.Bottom,
        "superior" or "top" or "sup" => BoardFace.Top,
        _ => board.PreferredFace,
    };

    var settings = MachineSettings.Load();
    var options = new CamOptions
    {
        Rotation = (Opt(a, "--girar") ?? "auto").ToLowerInvariant() switch
        {
            "no" => RotationMode.None,
            "si" or "sí" or "yes" => RotationMode.Rotate90,
            _ => RotationMode.Auto,
        },
        TravelXMm = settings.TravelXMm,
        TravelYMm = settings.TravelYMm,
    };
    if (Opt(a, "--recorrido") is { } rec)
    {
        var parts = rec.ToLowerInvariant().Split('x');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, inv, out double tx) || !double.TryParse(parts[1], NumberStyles.Float, inv, out double ty))
            return Fail("--recorrido debe ser como 100x140.");
        options.TravelXMm = tx;
        options.TravelYMm = ty;
    }

    var presets = CamPresetStore.Load();
    var preset = (Opt(a, "--preset") is { } pn
        ? presets.FirstOrDefault(p => p.Name.Contains(pn, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"No hay un preset que contenga \"{pn}\".")
        : presets[0]).Clone();
    if (OptD(a, "--tool") is { } tool) preset.ToolMm = tool;
    if (OptD(a, "--depth") is { } depth) preset.DepthMm = depth;
    if (OptD(a, "--passes") is { } passes) preset.Passes = (int)passes;
    if (OptD(a, "--step") is { } step) preset.StepMm = step;
    if (OptD(a, "--feed") is { } feed) preset.FeedMmMin = (int)feed;
    if (OptD(a, "--plunge") is { } plunge) preset.PlungeMmMin = (int)plunge;
    if (OptD(a, "--rpm") is { } rpm) preset.SpindleRpm = (int)rpm;
    if (OptD(a, "--thick") is { } thick) preset.BoardThicknessMm = thick;
    if (OptD(a, "--broca-unica") is { } bit) { preset.DrillSingleBit = true; preset.DrillSingleBitMm = bit; }

    var result = CamGenerator.Generate(board, face, preset, options);

    string outDir = Opt(a, "--salida") ?? (Directory.Exists(source) ? source : Path.GetDirectoryName(source)!);
    Directory.CreateDirectory(outDir);
    foreach (var (name, content) in result.Files)
        File.WriteAllText(Path.Combine(outDir, name), content);
    string report = CamReport.Build(result, preset, options, source);
    File.WriteAllText(Path.Combine(outDir, CamReport.FileNameFor(result)), report);

    Console.WriteLine(report);
    Console.WriteLine($"Escrito en: {outDir}");
    return result.FitsMachine ? 0 : 2;
}

int Repaso(string[] a)
{
    if (a.Length == 0 || a[0].StartsWith("--")) return Fail("Falta el archivo .nc.");
    string source = Path.GetFullPath(a[0]);
    var prog = RepasoProgram.Load(source);
    var selected = new List<int>();
    if (Opt(a, "--anillos") is { } rings)
        foreach (var s in rings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(s, out int n) || n < 1 || n > prog.Paths.Count)
                return Fail($"Anillo inválido: {s} (hay {prog.Paths.Count}, numerados desde 1).");
            selected.Add(n - 1);
        }
    var zones = new List<RectMm>();
    for (int i = 0; i < a.Length - 1; i++)
    {
        if (!a[i].Equals("--zona", StringComparison.OrdinalIgnoreCase)) continue;
        var v = a[i + 1].Split(',').Select(s => double.Parse(s, inv)).ToArray();
        if (v.Length != 4) return Fail("--zona debe ser x0,y0,x1,y1.");
        zones.Add(RectMm.FromCorners(v[0], v[1], v[2], v[3]));
    }
    var sections = new List<RepasoSection>();
    for (int i = 0; i < a.Length - 1; i++)
    {
        if (!a[i].Equals("--tramo", StringComparison.OrdinalIgnoreCase)) continue;
        var parts = a[i + 1].Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int n) || n < 1 || n > prog.Paths.Count)
            return Fail($"--tramo debe ser N:x0,y0:x1,y1 con N entre 1 y {prog.Paths.Count}.");
        var p0 = parts[1].Split(',').Select(s => double.Parse(s, inv)).ToArray();
        var p1 = parts[2].Split(',').Select(s => double.Parse(s, inv)).ToArray();
        if (p0.Length != 2 || p1.Length != 2) return Fail("--tramo debe ser N:x0,y0:x1,y1.");
        var path = prog.Paths[n - 1];
        var (from, to) = path.ChooseArc(path.Project(p0[0], p0[1]).U, path.Project(p1[0], p1[1]).U);
        sections.Add(new RepasoSection(n - 1, from, to));
    }
    double depth = OptD(a, "--depth") ?? prog.DepthMm;
    var export = prog.Export(selected, zones, sections, depth);
    string outPath = Opt(a, "--salida") ?? Path.Combine(Path.GetDirectoryName(source)!, RepasoProgram.SuggestedFileName(Path.GetFileName(source)));
    File.WriteAllText(outPath, export.Content);
    Console.WriteLine($"{prog.Paths.Count} trayectorias en {Path.GetFileName(source)}; exportados {export.Pieces} tramos " +
                      $"({export.LengthMm.ToString("0", inv)} mm, ~{Math.Ceiling(export.LengthMm / Math.Max(1, prog.FeedMmMin))} min) a {depth.ToString("0.0##", inv)} mm -> {outPath}");
    return 0;
}
