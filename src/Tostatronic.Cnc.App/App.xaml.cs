using System.Windows;

namespace Tostatronic.Cnc.App;

public partial class App : Application
{
    /// <summary>Argumentos de la línea de comandos: un .nc lo carga en la máquina; "--repaso archivo.nc" abre el repaso de pistas.</summary>
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        base.OnStartup(e);
    }
}
