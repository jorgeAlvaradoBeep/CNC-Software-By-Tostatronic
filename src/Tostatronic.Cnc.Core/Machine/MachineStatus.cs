namespace Tostatronic.Cnc.Core.Machine;

public enum MachineStatus
{
    SinConexion,
    Inactivo,
    Jog,
    Ejecutando,
    Pausado,
    Detenido,
    Terminado,
    Regresando,
    YendoACero,
    EsperandoHerramienta,
    Calibrando,
}

public static class MachineStatusExtensions
{
    public static string Texto(this MachineStatus s) => s switch
    {
        MachineStatus.SinConexion => "sin conexión",
        MachineStatus.Inactivo => "equipo preparado",
        MachineStatus.Jog => "moviendo",
        MachineStatus.Ejecutando => "ejecutando",
        MachineStatus.Pausado => "pausado",
        MachineStatus.Detenido => "detenido",
        MachineStatus.Terminado => "terminado",
        MachineStatus.Regresando => "regresando a la posición",
        MachineStatus.YendoACero => "yendo al cero",
        MachineStatus.EsperandoHerramienta => "cambio de herramienta",
        MachineStatus.Calibrando => "calibrando altura",
        _ => s.ToString(),
    };
}
