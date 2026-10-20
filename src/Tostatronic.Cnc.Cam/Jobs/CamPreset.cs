using System.Text.Json;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>
/// Parámetros de maquinado de PCB (equivalen a los argumentos de pcb_cnc.py). El preset
/// default trae los valores probados de Tostatronic: V-bit 0.3 mm con ancho efectivo 0.2,
/// profundidad 0.10, 2 pasadas a 0.15, F150/50, 10 000 rpm, placa 1.6 mm, barrenos F30,
/// contorno con fresa 1.0 mm en pasadas de 0.3 a F100.
/// </summary>
public sealed class CamPreset
{
    public string Name { get; set; } = "V-bit 0.3 mm (ancho efectivo 0.2) — probado";

    // aislamiento
    public double ToolMm { get; set; } = 0.2;     // ancho efectivo de la punta
    public double DepthMm { get; set; } = 0.10;
    public int Passes { get; set; } = 2;
    public double StepMm { get; set; } = 0.15;
    public int FeedMmMin { get; set; } = 150;
    public int PlungeMmMin { get; set; } = 50;
    public int SpindleRpm { get; set; } = 10000;

    // barrenos
    public double BoardThicknessMm { get; set; } = 1.6;
    public int DrillFeedMmMin { get; set; } = 30;
    /// <summary>true = un solo barreno/broca para todo; false = agrupar 0.8 / 1.0 / 1.2 como pcb_cnc.py.</summary>
    public bool DrillSingleBit { get; set; }
    public double DrillSingleBitMm { get; set; } = 1.0;

    // contorno
    public double ContourToolMm { get; set; } = 1.0;
    public double ContourStepMm { get; set; } = 0.3;
    public int ContourFeedMmMin { get; set; } = 100;

    // alturas
    public double SafeZMm { get; set; } = 2.0;
    public double TravelZMm { get; set; } = 15.0;

    public CamPreset Clone() => (CamPreset)MemberwiseClone();

    public override string ToString() => Name;
}

/// <summary>Lista de presets editable, persistida como JSON en %APPDATA%\TostatronicCnc.</summary>
public static class CamPresetStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "TostatronicCnc", "cam-presets.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<CamPreset> Defaults() => new()
    {
        new CamPreset(),
        new CamPreset
        {
            Name = "V-bit ancho efectivo 0.3",
            ToolMm = 0.3,
            StepMm = 0.25,
        },
        new CamPreset
        {
            Name = "V-bit ancho efectivo 0.1 (pistas finas)",
            ToolMm = 0.1,
            StepMm = 0.10,
            Passes = 3,
        },
    };

    public static List<CamPreset> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<CamPreset>>(File.ReadAllText(path), JsonOptions);
                if (list is { Count: > 0 })
                    return list;
            }
        }
        catch (Exception)
        {
            // archivo dañado: se regresa a los defaults
        }
        return Defaults();
    }

    public static void Save(List<CamPreset> presets, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(presets, JsonOptions));
    }
}
