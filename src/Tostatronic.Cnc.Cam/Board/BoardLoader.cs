using System.IO.Compression;
using Tostatronic.Cnc.Cam.Geometry;
using Tostatronic.Cnc.Cam.Gerber;

namespace Tostatronic.Cnc.Cam.Board;

/// <summary>
/// Carga una placa desde el ZIP de fabricación de EasyEDA (o cualquier carpeta/ZIP con
/// Gerbers y Excellon). Detecta las capas por el nombre del archivo: cobre superior
/// (.GTL / "TopLayer"), cobre inferior (.GBL / "BottomLayer"), contorno (.GKO / "Outline")
/// y barrenos (.DRL / .XLN / "Drill").
/// </summary>
public static class BoardLoader
{
    public static BoardModel LoadZip(string zipPath)
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
        var board = Load(entries);
        board.Name = CleanName(Path.GetFileNameWithoutExtension(zipPath));
        return board;
    }

    public static BoardModel LoadFolder(string dir)
    {
        var entries = Directory.EnumerateFiles(dir)
            .Select(f => (Path.GetFileName(f), (Func<string>)(() => File.ReadAllText(f))))
            .ToList();
        var board = Load(entries);
        board.Name = CleanName(Path.GetFileName(dir.TrimEnd('\\', '/')));
        return board;
    }

    private static string CleanName(string raw)
    {
        string name = raw;
        foreach (var prefix in new[] { "Gerber_", "gerber_", "Fabrication_", "PCB_" })
            if (name.StartsWith(prefix)) { name = name[prefix.Length..]; break; }
        name = name.Replace(' ', '_');
        return name.Length == 0 ? "placa" : name;
    }

    internal static BoardModel Load(IReadOnlyList<(string Name, Func<string> Read)> files)
    {
        var board = new BoardModel();
        GerberImage? top = null, bottom = null, outline = null;
        var drillTexts = new List<string>();

        foreach (var (name, read) in files)
        {
            string n = Path.GetFileName(name).ToLowerInvariant();
            string ext = Path.GetExtension(n);
            bool isMaskOrSilk = n.Contains("mask") || n.Contains("paste") || n.Contains("silk") ||
                                ext is ".gts" or ".gbs" or ".gto" or ".gbo" or ".gtp" or ".gbp";
            if (isMaskOrSilk) continue;

            if (ext == ".gbl" || n.Contains("bottomlayer") || n.Contains("bottom_layer") || n.Contains("copper_bottom"))
                bottom = ParseInto(board, name, read);
            else if (ext == ".gtl" || n.Contains("toplayer") || n.Contains("top_layer") || n.Contains("copper_top"))
                top = ParseInto(board, name, read);
            else if (ext == ".gko" || ext == ".gm1" || n.Contains("outline") || n.Contains("boardoutline") || n.Contains("edge"))
                outline = ParseInto(board, name, read);
            else if (ext is ".drl" or ".xln" or ".exc" || (ext == ".txt" && n.Contains("drill")))
                drillTexts.Add(read());
        }

        if (top == null && bottom == null)
            throw new InvalidDataException(
                "No se encontró ninguna capa de cobre (.GTL/.GBL, \"TopLayer\"/\"BottomLayer\") en los archivos.");

        board.CopperTop = top?.Filled;
        board.CopperBottom = bottom?.Filled;

        foreach (var text in drillTexts)
            board.Drills.AddRange(ExcellonParser.Parse(text, board.Warnings));
        if (drillTexts.Count == 0)
            board.Warnings.Add("No se encontró archivo de barrenos (Excellon .DRL): no habrá trabajo de taladrado.");

        // contorno: encadenar los trazos de la capa de contorno
        if (outline != null)
        {
            board.Outline = OutlineChainer.Chain(outline.Segments, board.Warnings);
            if (outline.Filled.Count > 0 && board.Outline.Count == 0)
                board.Outline = outline.Filled; // contorno dibujado como región
        }
        else
        {
            board.Warnings.Add("No se encontró la capa de contorno (.GKO): se usará el rectángulo envolvente del cobre.");
        }

        // límites
        var boundsSource = board.Outline.Count > 0
            ? board.Outline
            : Geo.Union(board.CopperBottom ?? board.CopperTop!);
        if (boundsSource.Count == 0)
            throw new InvalidDataException("Los archivos no contienen geometría.");
        var (minX, minY, maxX, maxY) = Geo.BoundsMm(boundsSource);
        board.MinX = minX; board.MinY = minY; board.MaxX = maxX; board.MaxY = maxY;

        if (board.Outline.Count == 0)
        {
            // rectángulo envolvente del cobre como contorno de corte
            board.Outline = new Clipper2Lib.Paths64
            {
                new Clipper2Lib.Path64
                {
                    Geo.Pt(minX, minY), Geo.Pt(maxX, minY), Geo.Pt(maxX, maxY), Geo.Pt(minX, maxY),
                },
            };
        }
        return board;
    }

    private static GerberImage ParseInto(BoardModel board, string name, Func<string> read)
    {
        var img = GerberParser.Parse(read());
        foreach (var w in img.Warnings)
            board.Warnings.Add($"{Path.GetFileName(name)}: {w}");
        return img;
    }
}
