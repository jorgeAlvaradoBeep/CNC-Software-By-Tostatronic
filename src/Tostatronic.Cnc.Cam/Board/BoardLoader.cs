using System.Globalization;
using System.IO.Compression;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Geometry;
using Tostatronic.Cnc.Cam.Gerber;

namespace Tostatronic.Cnc.Cam.Board;

/// <summary>Opciones de carga y validación de la placa.</summary>
public sealed class BoardLoadOptions
{
    /// <summary>Descartar las islas de cobre que quedan lejos de la placa (pistas olvidadas fuera del diseño).</summary>
    public bool DropStrayCopper { get; set; } = true;

    /// <summary>A partir de qué separación (mm) una isla de cobre se considera "fuera de la placa" cuando no hay contorno.</summary>
    public double StrayDistanceMm { get; set; } = 10.0;

    /// <summary>Margen (mm) alrededor del cobre para el contorno cuando los archivos no traen capa de contorno.</summary>
    public double OutlineMarginMm { get; set; } = 2.0;

    /// <summary>Anillo de cobre mínimo (mm) alrededor de un barreno antes de avisar.</summary>
    public double MinAnnularRingMm { get; set; } = 0.25;
}

/// <summary>
/// Carga una placa desde el ZIP de fabricación de EasyEDA, Altium Designer o KiCad (o cualquier
/// carpeta/ZIP con Gerbers y Excellon). Detecta las capas por el nombre del archivo: cobre
/// superior (.GTL / "TopLayer" / "Copper_Signal_Top" / "F_Cu"), cobre inferior (.GBL /
/// "BottomLayer" / "Copper_Signal_Bot" / "B_Cu"), contorno (.GKO / .GM1 / "Outline" / "Edge_Cuts")
/// y barrenos (Excellon .DRL/.XLN/.TXT, o un Gerber con "Drill" en el nombre como el PTH_Drill.gbr
/// de Altium, donde cada flash es un barreno). Máscaras, serigrafía, "Pads" y reportes se ignoran.
/// Además valida: una o dos capas, cobre suelto fuera de la placa, barrenos fuera del cobre,
/// anillos de cobre muy delgados y contorno faltante.
/// </summary>
public static class BoardLoader
{
    public static BoardModel LoadZip(string zipPath, BoardLoadOptions? options = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Where(e => e.Length > 0)
            .Select(e => (e.FullName, (Func<string>)(() =>
            {
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            })))
            .ToList();
        var board = Load(entries, options);
        board.Name = CleanName(Path.GetFileNameWithoutExtension(zipPath));
        return board;
    }

    public static BoardModel LoadFolder(string dir, BoardLoadOptions? options = null)
    {
        var entries = Directory.EnumerateFiles(dir)
            .Select(f => (Path.GetFileName(f), (Func<string>)(() => File.ReadAllText(f))))
            .ToList();
        var board = Load(entries, options);
        board.Name = CleanName(Path.GetFileName(dir.TrimEnd('\\', '/')));
        return board;
    }

    /// <summary>Carga un ZIP o una carpeta según lo que sea la ruta.</summary>
    public static BoardModel LoadPath(string path, BoardLoadOptions? options = null) =>
        Directory.Exists(path) ? LoadFolder(path, options) : LoadZip(path, options);

    internal static string CleanName(string raw)
    {
        string name = raw;
        foreach (var prefix in new[] { "Gerber_", "gerber_", "Fabrication_", "PCB_" })
            if (name.StartsWith(prefix)) { name = name[prefix.Length..]; break; }
        // Altium agrega "-macro" al ZIP de salida; la fecha de exportación tampoco aporta al nombre del .nc
        if (name.EndsWith("-macro", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[_-]\d{4}-\d{2}-\d{2}$", "");
        name = name.Replace(' ', '_');
        return name.Length == 0 ? "placa" : name;
    }

    public enum FileKind { Skip, CopperTop, CopperBottom, Outline, Excellon, GerberDrill }

    /// <summary>Clasifica un archivo por su nombre (y, para los .txt, por su contenido).</summary>
    public static FileKind Classify(string fileName, Func<string> read)
    {
        string n = Path.GetFileName(fileName).ToLowerInvariant();
        string ext = Path.GetExtension(n);

        // reportes y auxiliares (Altium: .apr .rep .rul .extrep .apr_lib .ldp .drr; KiCad: .gbrjob; varios)
        if (ext is ".apr" or ".rep" or ".rul" or ".extrep" or ".apr_lib" or ".ldp" or ".drr" or ".gbrjob" or ".csv"
            or ".json" or ".pdf" or ".png" or ".md" or ".html" or ".xml")
            return FileKind.Skip;
        // máscaras, pasta, serigrafía, "pad master" de Altium, dibujos de taladro, ensamble
        bool isAux = n.Contains("mask") || n.Contains("paste") || n.Contains("silk") || n.Contains("legend") ||
                     n.Contains("overlay") || n.Contains("pads_") || n.Contains("pad_master") || n.Contains("padmaster") ||
                     n.Contains("drill_drawing") || n.Contains("drillguide") || n.Contains("drill_guide") || n.Contains("drilldrawing") ||
                     n.Contains("courtyard") || n.Contains("assembly") || n.Contains("fab_") || n.Contains("_fab") ||
                     ext is ".gts" or ".gbs" or ".gto" or ".gbo" or ".gtp" or ".gbp" or ".gd1" or ".gg1" or ".gpt" or ".gpb";
        if (isAux) return FileKind.Skip;

        bool isGerberExt = ext is ".gbr" or ".gbx" or ".ger" or ".pho" or ".art" or ".gtl" or ".gbl" or ".gko" or ".gm1";

        // barrenos
        if (ext is ".drl" or ".xln" or ".exc" or ".drd" or ".ncd")
            return FileKind.Excellon;
        if (n.Contains("drill") || n.Contains("barreno") || n.Contains("holes") || n.Contains("npth") || n.Contains("-pth"))
        {
            if (isGerberExt) return FileKind.GerberDrill;
            if (ext == ".txt") return LooksLikeExcellon(read()) ? FileKind.Excellon : FileKind.GerberDrill;
            return FileKind.Excellon;
        }
        if (ext == ".txt")
            return LooksLikeExcellon(read()) ? FileKind.Excellon : FileKind.Skip;

        // cobre
        if (ext == ".gbl" || n.Contains("bottomlayer") || n.Contains("bottom_layer") || n.Contains("copper_bottom") ||
            n.Contains("b_cu") || n.Contains("b.cu") || (n.Contains("copper") && n.Contains("bot")))
            return FileKind.CopperBottom;
        if (ext == ".gtl" || n.Contains("toplayer") || n.Contains("top_layer") || n.Contains("copper_top") ||
            n.Contains("f_cu") || n.Contains("f.cu") || (n.Contains("copper") && n.Contains("top")))
            return FileKind.CopperTop;

        // contorno
        if (ext == ".gko" || ext == ".gm1" || n.Contains("outline") || n.Contains("boardoutline") || n.Contains("edge") ||
            n.Contains("mechanical1") || n.Contains("mechanical_1") || n.Contains("keep-out") || n.Contains("keepout"))
            return FileKind.Outline;

        return FileKind.Skip;
    }

    private static bool LooksLikeExcellon(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("M48") || head.Contains("METRIC") || head.Contains("INCH") || head.Contains("FMAT");
    }

    private static string DetectSource(IEnumerable<string> names)
    {
        var lower = names.Select(x => Path.GetFileName(x).ToLowerInvariant()).ToList();
        if (lower.Any(x => x.EndsWith(".apr") || x.EndsWith(".extrep") || x.Contains("copper_signal") || x.Contains("pads_")))
            return "Altium Designer";
        if (lower.Any(x => x.Contains("_cu.gbr") || x.EndsWith(".gbrjob") || x.Contains("edge_cuts")))
            return "KiCad";
        if (lower.Any(x => x.StartsWith("gerber_") && (x.EndsWith(".gtl") || x.EndsWith(".gbl") || x.EndsWith(".gko"))))
            return "EasyEDA";
        return "desconocido";
    }

    internal static BoardModel Load(IReadOnlyList<(string Name, Func<string> Read)> files, BoardLoadOptions? options = null)
    {
        options ??= new BoardLoadOptions();
        var board = new BoardModel { Source = DetectSource(files.Select(f => f.Name)) };
        GerberImage? top = null, bottom = null, outline = null;
        var drillTexts = new List<(string Name, string Text)>();
        var gerberDrills = new List<(string Name, GerberImage Img)>();
        string? topName = null, bottomName = null;

        foreach (var (name, read) in files)
        {
            switch (Classify(name, read))
            {
                case FileKind.CopperBottom:
                    bottom = ParseInto(board, name, read); bottomName = Path.GetFileName(name); break;
                case FileKind.CopperTop:
                    top = ParseInto(board, name, read); topName = Path.GetFileName(name); break;
                case FileKind.Outline:
                    outline = ParseInto(board, name, read); break;
                case FileKind.Excellon:
                    drillTexts.Add((Path.GetFileName(name), read())); break;
                case FileKind.GerberDrill:
                    gerberDrills.Add((Path.GetFileName(name), ParseInto(board, name, read))); break;
            }
        }

        if (top == null && bottom == null)
            throw new InvalidDataException(
                "No se encontró ninguna capa de cobre (.GTL/.GBL, \"TopLayer\"/\"BottomLayer\", \"Copper_Signal_Top/Bot\") en los archivos.");

        board.CopperTop = top?.Filled;
        board.CopperBottom = bottom?.Filled;
        if (top != null) { board.PadsTop.AddRange(top.Flashes); board.TopHasTraces = top.HasTraces; }
        if (bottom != null) { board.PadsBottom.AddRange(bottom.Flashes); board.BottomHasTraces = bottom.HasTraces; }
        board.Info.Add($"Archivos de {board.Source}.");

        // ---- una o dos capas ----
        DescribeLayers(board, top, bottom, topName, bottomName);

        // ---- barrenos ----
        foreach (var (name, text) in drillTexts)
        {
            int before = board.Drills.Count;
            board.Drills.AddRange(ExcellonParser.Parse(text, board.Warnings));
            board.Info.Add($"{board.Drills.Count - before} barrenos de {name} (Excellon).");
        }
        foreach (var (name, img) in gerberDrills)
        {
            int before = board.Drills.Count;
            board.Drills.AddRange(DrillsFromGerber(img, board.Warnings, name));
            board.Info.Add($"{board.Drills.Count - before} barrenos de {name} (Gerber de taladro: cada flash es un barreno con el diámetro de su apertura).");
        }
        if (drillTexts.Count == 0 && gerberDrills.Count == 0)
            board.Warnings.Add("No se encontró archivo de barrenos (Excellon .DRL ni Gerber \"Drill\"): no habrá trabajo de taladrado.");

        // ---- contorno ----
        if (outline != null)
        {
            board.Outline = OutlineChainer.Chain(outline.Segments, board.Warnings);
            if (outline.Filled.Count > 0 && board.Outline.Count == 0)
                board.Outline = outline.Filled; // contorno dibujado como región
        }

        // ---- cobre suelto fuera de la placa ----
        FindStrayCopper(board, options);

        // ---- límites ----
        Paths64 allCopper = Geo.Union(Concat(board.CopperBottom, board.CopperTop));
        if (allCopper.Count == 0)
            throw new InvalidDataException("Los archivos no contienen geometría de cobre.");
        if (board.Outline.Count > 0)
        {
            var (minX, minY, maxX, maxY) = Geo.BoundsMm(board.Outline);
            board.MinX = minX; board.MinY = minY; board.MaxX = maxX; board.MaxY = maxY;
            var (cx0, cy0, cx1, cy1) = Geo.BoundsMm(allCopper);
            if (cx0 < minX - 0.01 || cy0 < minY - 0.01 || cx1 > maxX + 0.01 || cy1 > maxY + 0.01)
                board.Warnings.Add("Hay cobre que se sale del contorno de la placa.");
        }
        else
        {
            var (minX, minY, maxX, maxY) = Geo.BoundsMm(allCopper);
            double m = Math.Max(0, options.OutlineMarginMm);
            board.MinX = minX - m; board.MinY = minY - m; board.MaxX = maxX + m; board.MaxY = maxY + m;
            board.OutlineSynthetic = true;
            board.Outline = new Paths64
            {
                new Path64
                {
                    Geo.Pt(board.MinX, board.MinY), Geo.Pt(board.MaxX, board.MinY),
                    Geo.Pt(board.MaxX, board.MaxY), Geo.Pt(board.MinX, board.MaxY),
                },
            };
            board.Warnings.Add(
                "No se encontró la capa de contorno (.GKO / Mechanical / Edge_Cuts): se usará el rectángulo del cobre más " +
                $"{F(m, "0.#")} mm por lado ({F(board.WidthMm, "0.0")} × {F(board.HeightMm, "0.0")} mm). " +
                "Si la placa real es más grande, exporta la capa de contorno o cambia el margen.");
        }

        CheckDrills(board, options);
        return board;
    }

    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    private static Paths64 Concat(Paths64? a, Paths64? b)
    {
        var r = new Paths64();
        if (a != null) r.AddRange(a);
        if (b != null) r.AddRange(b);
        return r;
    }

    private static void DescribeLayers(BoardModel board, GerberImage? top, GerberImage? bottom, string? topName, string? bottomName)
    {
        static string Desc(GerberImage img) =>
            $"{img.Flashes.Count} pads, {img.Segments.Count} trazos, {img.RegionCount} regiones";
        if (top != null) board.Info.Add($"Cobre superior ({topName}): {Desc(top)}.");
        if (bottom != null) board.Info.Add($"Cobre inferior ({bottomName}): {Desc(bottom)}.");

        if (top != null && bottom != null)
        {
            if (board.TopHasTraces && !board.BottomHasTraces)
                board.Info.Add("Placa de UNA capa: las pistas están en la cara SUPERIOR; la inferior solo trae los pads de los " +
                               "componentes pasantes (el CAD los proyecta a ambas caras). Se fresa la cara superior, sin espejo.");
            else if (board.BottomHasTraces && !board.TopHasTraces)
                board.Info.Add("Placa de UNA capa: las pistas están en la cara INFERIOR; la superior solo trae los pads de los " +
                               "componentes pasantes. Se fresa la cara inferior, en espejo.");
            else if (board.TopHasTraces && board.BottomHasTraces)
                board.Warnings.Add("Placa de DOS capas: las dos caras traen pistas. Hay que fresar las dos y voltear con calibración.");
            else
                board.Warnings.Add("Ninguna cara trae pistas, solo pads.");
        }
        else
        {
            var face = top != null ? "superior" : "inferior";
            board.Info.Add($"Placa de UNA capa: solo vino el cobre {face}" +
                           (board.TopHasTraces || board.BottomHasTraces ? "." : " (y sin pistas, solo pads)."));
        }
    }

    /// <summary>Un Gerber de taladro (Altium PTH_Drill.gbr): cada flash con apertura circular es un barreno.</summary>
    internal static List<DrillHole> DrillsFromGerber(GerberImage img, List<string> warnings, string name)
    {
        var holes = new List<DrillHole>(img.Flashes.Count);
        bool nonRound = false;
        foreach (var f in img.Flashes)
        {
            if (f.Kind != 'C') nonRound = true;
            double d = f.MinSize;
            if (d <= 0) continue;
            holes.Add(new DrillHole(f.X, f.Y, d));
        }
        if (nonRound)
            warnings.Add($"{name}: hay barrenos con apertura no circular; se usa el lado menor como diámetro.");
        if (img.Segments.Count > 0)
            warnings.Add($"{name}: el archivo de taladro trae trazos (ranuras o dibujo): solo se toman los flashes como barrenos.");
        return holes;
    }

    /// <summary>
    /// Islas de cobre lejos del resto: con contorno, lo que queda fuera de él; sin contorno, lo que está a más
    /// de StrayDistanceMm del cuerpo principal (que crece desde el polígono más grande). Se avisa y, si
    /// DropStrayCopper, se quitan de las caras para que no agranden la placa ni generen trabajo.
    /// </summary>
    private static void FindStrayCopper(BoardModel board, BoardLoadOptions o)
    {
        var all = Geo.Union(Concat(board.CopperBottom, board.CopperTop));
        var outers = all.Where(p => Clipper.IsPositive(p) && p.Count >= 3).ToList();
        if (outers.Count <= 1) return;

        var bounds = outers.Select(p => Clipper.GetBounds(new Paths64 { p })).ToList();
        var stray = new List<int>();
        Rect64 body;
        if (board.Outline.Count > 0)
        {
            body = Clipper.GetBounds(board.Outline);
            long tol = Geo.L(0.5);
            for (int i = 0; i < outers.Count; i++)
            {
                var b = bounds[i];
                if (b.right < body.left - tol || b.left > body.right + tol || b.bottom < body.top - tol || b.top > body.bottom + tol)
                    stray.Add(i);
            }
        }
        else
        {
            // cúmulos: polígonos a menos de StrayDistanceMm entre sí (por sus rectángulos); el cuerpo
            // principal es el cúmulo con más área de cobre y lo demás es cobre suelto
            var parent = Enumerable.Range(0, outers.Count).ToArray();
            int Find(int k) { while (parent[k] != k) k = parent[k] = parent[parent[k]]; return k; }
            double gap = o.StrayDistanceMm * Geo.Scale;
            for (int i = 0; i < outers.Count; i++)
                for (int j = i + 1; j < outers.Count; j++)
                    if (RectGap(bounds[i], bounds[j]) <= gap)
                        parent[Find(i)] = Find(j);
            var area = new Dictionary<int, double>();
            for (int i = 0; i < outers.Count; i++)
            {
                int root = Find(i);
                area[root] = area.GetValueOrDefault(root) + Math.Abs(Clipper.Area(outers[i]));
            }
            int mainRoot = area.OrderByDescending(kv => kv.Value).First().Key;
            body = bounds[mainRoot];
            for (int i = 0; i < outers.Count; i++)
            {
                if (Find(i) != mainRoot) { stray.Add(i); continue; }
                body = new Rect64(Math.Min(body.left, bounds[i].left), Math.Min(body.top, bounds[i].top),
                                  Math.Max(body.right, bounds[i].right), Math.Max(body.bottom, bounds[i].bottom));
            }
        }
        foreach (int i in stray)
            board.Stray.Add(ToStray(bounds[i], RectGap(body, bounds[i]) / Geo.Scale));
        if (board.Stray.Count == 0) return;

        var where = string.Join("; ", board.Stray.Take(5).Select(s =>
            $"{F(s.WidthMm, "0.0")}×{F(s.HeightMm, "0.0")} mm en X{F(s.CenterX, "0.0")} Y{F(s.CenterY, "0.0")} del diseño, " +
            $"a {F(s.DistanceMm, "0")} mm de la placa"));
        if (o.DropStrayCopper)
        {
            board.Warnings.Add($"Cobre suelto FUERA de la placa ({board.Stray.Count}): {where}. Se descartó para que no agrande la placa; " +
                               "bórralo en el CAD.");
            var strayBounds = stray.Select(i => bounds[i]).ToList();
            board.CopperTop = board.CopperTop == null ? null : DropInside(board.CopperTop, strayBounds);
            board.CopperBottom = board.CopperBottom == null ? null : DropInside(board.CopperBottom, strayBounds);
            board.PadsTop.RemoveAll(f => strayBounds.Any(b => Contains(b, f.X, f.Y)));
            board.PadsBottom.RemoveAll(f => strayBounds.Any(b => Contains(b, f.X, f.Y)));
        }
        else
        {
            board.Warnings.Add($"Cobre suelto FUERA de la placa ({board.Stray.Count}): {where}. Se conserva (opción), así que la placa " +
                               "sale más grande de lo que debería.");
        }
    }

    private static StrayCopper ToStray(Rect64 b, double dist) =>
        new(Geo.Mm(b.left), Geo.Mm(b.top), Geo.Mm(b.right), Geo.Mm(b.bottom), dist);

    /// <summary>Separación entre dos rectángulos (0 si se tocan o traslapan), en unidades de Clipper.</summary>
    private static double RectGap(Rect64 a, Rect64 b)
    {
        double dx = Math.Max(0, Math.Max(b.left - a.right, a.left - b.right));
        double dy = Math.Max(0, Math.Max(b.top - a.bottom, a.top - b.bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool Contains(Rect64 b, double xMm, double yMm)
    {
        long x = Geo.L(xMm), y = Geo.L(yMm), tol = Geo.L(0.01);
        return x >= b.left - tol && x <= b.right + tol && y >= b.top - tol && y <= b.bottom + tol;
    }

    private static Paths64 DropInside(Paths64 paths, List<Rect64> boxes)
    {
        var kept = new Paths64();
        long tol = Geo.L(0.01);
        foreach (var p in paths)
        {
            var b = Clipper.GetBounds(new Paths64 { p });
            bool inside = boxes.Any(box => b.left >= box.left - tol && b.right <= box.right + tol &&
                                           b.top >= box.top - tol && b.bottom <= box.bottom + tol);
            if (!inside) kept.Add(p);
        }
        return kept;
    }

    /// <summary>Barrenos fuera del cobre de la cara a fresar y anillos de cobre demasiado delgados.</summary>
    private static void CheckDrills(BoardModel board, BoardLoadOptions o)
    {
        if (board.Drills.Count == 0) return;
        var face = board.PreferredFace;
        var copper = face == BoardFace.Bottom ? board.CopperBottom : board.CopperTop;
        var pads = face == BoardFace.Bottom ? board.PadsBottom : board.PadsTop;
        if (copper == null) return;

        int outside = 0;
        var thin = new List<(DrillHole Hole, double Ring)>();
        foreach (var h in board.Drills)
        {
            if (!Geo.InsideFilled(Geo.Pt(h.X, h.Y), copper)) { outside++; continue; }
            // pad que lo rodea: el flash más cercano a menos de 0.1 mm del centro
            GerberFlash? pad = null;
            double best = 0.1;
            foreach (var f in pads)
            {
                double d = double.Hypot(f.X - h.X, f.Y - h.Y);
                if (d < best) { best = d; pad = f; }
            }
            if (pad is { } p)
            {
                double ring = (p.MinSize - h.Diameter) / 2;
                if (ring < o.MinAnnularRingMm) thin.Add((h, ring));
            }
        }
        if (outside > 0)
            board.Warnings.Add($"{outside} barreno(s) caen fuera del cobre de la cara a fresar (¿barreno de montaje sin pad, o capas desalineadas?).");
        if (thin.Count > 0)
        {
            double minRing = thin.Min(t => t.Ring);
            var sample = thin.OrderBy(t => t.Ring).First().Hole;
            board.Warnings.Add($"{thin.Count} pad(s) con anillo de cobre menor a {F(o.MinAnnularRingMm, "0.0#")} mm " +
                               $"(mínimo {F(minRing, "0.00")} mm, p. ej. barreno de {F(sample.Diameter, "0.00")} en " +
                               $"X{F(sample.X, "0.0")} Y{F(sample.Y, "0.0")} del diseño): con la broca de 0.8 el pad puede quedar sin cobre.");
        }
    }

    private static GerberImage ParseInto(BoardModel board, string name, Func<string> read)
    {
        var img = GerberParser.Parse(read());
        foreach (var w in img.Warnings)
            board.Warnings.Add($"{Path.GetFileName(name)}: {w}");
        return img;
    }
}
