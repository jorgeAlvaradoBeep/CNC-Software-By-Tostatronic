using System.Text.Json;

namespace Tostatronic.Cnc.Core.Leveling;

/// <summary>
/// Guarda y carga el mapa de alturas como JSON junto al G-code (<c>archivo.nc.nivel.json</c>).
/// Ojo: el mapa es relativo a los ceros de trabajo con los que se sondeó; al cargarlo en otra
/// sesión solo es válido si la placa y los ceros X0 Y0 / Z0 son los mismos.
/// </summary>
public static class LevelingMapFile
{
    private sealed record CellDto(int Col, int Row, int Steps);

    private sealed record MapDto(double RasterMm, long CellStepsX, long CellStepsY,
                                 long MinXSteps, long MaxYSteps, double MmPerStepZ,
                                 List<CellDto> Offsets);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string gcodePath) => gcodePath + ".nivel.json";

    public static void Save(LevelingMap map, string path)
    {
        var dto = new MapDto(map.RasterMm, map.CellStepsX, map.CellStepsY,
                             map.MinXSteps, map.MaxYSteps, map.MmPerStepZ,
                             map.Offsets.Select(kv => new CellDto(kv.Key.Col, kv.Key.Row, kv.Value)).ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    /// <summary>Carga un mapa guardado; null si el archivo no existe o está dañado.</summary>
    public static LevelingMap? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<MapDto>(File.ReadAllText(path), JsonOptions);
            if (dto == null || dto.Offsets.Count == 0) return null;
            var map = new LevelingMap(dto.RasterMm, dto.CellStepsX, dto.CellStepsY,
                                      dto.MinXSteps, dto.MaxYSteps, dto.MmPerStepZ);
            foreach (var c in dto.Offsets)
                map.SetOffset(new GridCell(c.Col, c.Row), c.Steps);
            return map;
        }
        catch (Exception)
        {
            return null; // archivo dañado o de otra versión: se ignora
        }
    }
}
