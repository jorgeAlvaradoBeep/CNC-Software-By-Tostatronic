using System.Text.Json;
using System.Text.Json.Serialization;
using Tostatronic.Cnc.Core.Motion;

namespace Tostatronic.Cnc.Core.Config;

/// <summary>
/// Ajustes de máquina equivalentes a los de Wegstr (ver docs/wegstr-analisis.md §6),
/// persistidos como JSON editable.
/// </summary>
public sealed class MachineSettings
{
    /// <summary>Pasos por 10 mm por eje ("corrección ligera" de Wegstr, 2400–2600, default 2500 → 0.004 mm/paso).</summary>
    public int StepsPer10mmX { get; set; } = 2500;
    public int StepsPer10mmY { get; set; } = 2500;
    public int StepsPer10mmZ { get; set; } = 2500;

    /// <summary>Valor del paquete 0x7F (2 = PC rápida .. 20 = lenta).</summary>
    public int Performance { get; set; } = 4;

    public double GoZeroSafeHeightMm { get; set; } = 5.0;
    public bool GoZeroSafeHeightEnabled { get; set; } = false;

    /// <summary>Encender el husillo automáticamente al iniciar un trabajo.</summary>
    public bool AutoSpindle { get; set; } = false;

    public int StepsPer10mm(Axis axis) => axis switch
    {
        Axis.X => StepsPer10mmX,
        Axis.Y => StepsPer10mmY,
        _ => StepsPer10mmZ,
    };

    public double MmPerStep(Axis axis) => 10.0 / StepsPer10mm(axis);

    public long StepsFromMm(Axis axis, double mm) => (long)Math.Round(mm / MmPerStep(axis));

    public double MmFromSteps(Axis axis, long steps) => steps * MmPerStep(axis);

    // ---- persistencia ----

    [JsonIgnore]
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "TostatronicCnc", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MachineSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<MachineSettings>(File.ReadAllText(path), JsonOptions) ?? new MachineSettings();
        }
        catch (Exception)
        {
            // archivo corrupto: se regresa a los valores por defecto
        }
        return new MachineSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
