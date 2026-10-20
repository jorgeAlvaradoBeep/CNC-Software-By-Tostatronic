namespace Tostatronic.Cnc.Tests;

public static class Fixtures
{
    /// <summary>Carpeta "examples gcode" del repo, buscada hacia arriba desde el binario de pruebas.</summary>
    public static string ExamplesDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "examples gcode");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("No se encontró la carpeta 'examples gcode'.");
        }
    }

    public static string Example(string name) => Path.Combine(ExamplesDir, name);

    /// <summary>Carpeta "examples gerber" del repo.</summary>
    public static string GerberExample(string name) =>
        Path.Combine(Path.GetDirectoryName(ExamplesDir)!, "examples gerber", name);
}
