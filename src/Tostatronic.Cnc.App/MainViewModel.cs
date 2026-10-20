using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Tostatronic.Cnc.Core.Machine;
using Tostatronic.Cnc.Core.Motion;
using Tostatronic.Cnc.Core.Protocol;

namespace Tostatronic.Cnc.App;

public partial class MainViewModel : ObservableObject
{
    public MachineController Machine { get; } = new();

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _uiTimer;

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<string> ConsoleLines { get; } = new();

    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private bool simulationMode;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string statusText = "sin conexión";
    [ObservableProperty] private string connectText = "Conectar";
    [ObservableProperty] private string xText = "+0.000";
    [ObservableProperty] private string yText = "+0.000";
    [ObservableProperty] private string zText = "+0.000";
    [ObservableProperty] private bool spindleOn;
    [ObservableProperty] private double speedPercent = 100;
    [ObservableProperty] private string speedText = "100 %";
    [ObservableProperty] private string fileName = "(sin archivo)";
    [ObservableProperty] private string fileInfo = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string currentLineText = "";
    [ObservableProperty] private string elapsedText = "00:00:00";
    [ObservableProperty] private bool detailedLog;
    [ObservableProperty] private string lastWarning = "";
    [ObservableProperty] private bool probeLatch;

    /// <summary>Paso del jog en mm; null = jog continuo.</summary>
    public double? JogStepMm { get; set; }

    public MainViewModel()
    {
        Machine.Changed += () => _dispatcher.BeginInvoke(RefreshFromMachine);
        Machine.Log += msg => AddConsole(msg);
        Machine.Warning += msg =>
        {
            AddConsole("⚠ " + msg);
            _dispatcher.BeginInvoke(() => LastWarning = msg);
        };
        Machine.RawTraffic += (data, outgoing) =>
        {
            if (DetailedLog)
                AddConsole((outgoing ? "TX " : "RX ") + BitConverter.ToString(data));
        };

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => RefreshJobInfo();
        _uiTimer.Start();

        RefreshPorts();
        SimulationMode = Ports.Count == 0;
    }

    partial void OnSpeedPercentChanged(double value)
    {
        int pct = (int)Math.Clamp(value, 10, 100);
        Machine.SpeedPercent = pct;
        SpeedText = $"{pct} %";
    }

    partial void OnSimulationModeChanged(bool value) => Machine.SimulationMode = value;

    private void AddConsole(string msg)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ConsoleLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (ConsoleLines.Count > 500)
                ConsoleLines.RemoveAt(0);
            ConsoleScrollRequested?.Invoke();
        });
    }

    public event Action? ConsoleScrollRequested;

    private void RefreshFromMachine()
    {
        IsConnected = Machine.IsConnected;
        ConnectText = IsConnected ? "Desconectar" : "Conectar";
        StatusText = Machine.Status.Texto() + (Machine.SimulationMode && IsConnected ? "  ·  SIMULACIÓN" : "");
        XText = Machine.Xmm.ToString("+0.000;-0.000");
        YText = Machine.Ymm.ToString("+0.000;-0.000");
        ZText = Machine.Zmm.ToString("+0.000;-0.000");
        SpindleOn = Machine.SpindleOn;
        ProbeLatch = Machine.ProbeLatch;
        RefreshJobInfo();
    }

    private void RefreshJobInfo()
    {
        var job = Machine.Job;
        if (job == null)
        {
            Progress = 0;
            ProgressText = "";
            CurrentLineText = "";
            ElapsedText = "00:00:00";
            return;
        }
        Progress = job.ProgressPercent;
        ProgressText = $"{job.ProgressPercent:F0} %";
        CurrentLineText = job.CurrentSourceLine > 0
            ? $"Línea {job.CurrentSourceLine}: {job.CurrentLineText}"
            : "";
        ElapsedText = job.Elapsed.ToString(@"hh\:mm\:ss");
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var names = SerialPortLink.GetPortNames();
        Ports.Clear();
        foreach (var n in names) Ports.Add(n);
        if (SelectedPort == null || !Ports.Contains(SelectedPort))
            SelectedPort = Ports.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (SimulationMode)
        {
            AddConsole("Modo simulación activo: no hace falta buscar, solo Conectar.");
            return;
        }
        RefreshPorts();
        var found = await Machine.SearchPortsAsync();
        if (found != null)
        {
            if (!Ports.Contains(found)) Ports.Add(found);
            SelectedPort = found;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (Machine.IsConnected)
        {
            Machine.Disconnect();
            return;
        }
        await Machine.ConnectAsync(SelectedPort);
    }

    [RelayCommand]
    private void Load()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Cargar G-code",
            Filter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Machine.LoadProgram(dlg.FileName);
            FileName = Path.GetFileName(dlg.FileName);
            var p = Machine.Program!;
            FileInfo = $"{p.RawLines.Count} líneas · X {p.MinX:F1}…{p.MaxX:F1} · Y {p.MinY:F1}…{p.MaxY:F1} · Z mín {p.MinZ:F2} mm";
        }
        catch (Exception ex)
        {
            LastWarning = ex.Message;
            AddConsole("⚠ " + ex.Message);
        }
    }

    [RelayCommand] private void Start() => Machine.StartOrResumeJob();
    [RelayCommand] private void Pause() => Machine.PauseJob();
    [RelayCommand] private void StopJob() => Machine.StopJob();
    [RelayCommand] private void Rewind() => Machine.RewindJob();
    [RelayCommand] private async Task GoZeroAsync() => await Machine.GoToZeroAsync();
    [RelayCommand] private async Task ToggleSpindleAsync() => await Machine.SetSpindleAsync(!Machine.SpindleOn);
    [RelayCommand] private void ZeroX() => Machine.ZeroAxis(Axis.X);
    [RelayCommand] private void ZeroY() => Machine.ZeroAxis(Axis.Y);
    [RelayCommand] private void ZeroZ() => Machine.ZeroAxis(Axis.Z);
    [RelayCommand] private void ZeroAll() => Machine.ZeroAll();
    [RelayCommand] private void EStop() => Machine.EmergencyStop();
    [RelayCommand] private void ClearWarning() => LastWarning = "";

    // ---- jog desde la vista (presionar/soltar) ----

    public void JogPress(Axis axis, int dir)
    {
        if (JogStepMm is double mm)
            _ = Machine.JogStepAsync(axis, dir, mm);
        else
            Machine.StartJog(axis, dir);
    }

    public void JogRelease(Axis axis)
    {
        if (JogStepMm == null)
            Machine.StopJog(axis);
    }

    public void Shutdown()
    {
        _uiTimer.Stop();
        Machine.Dispose();
    }
}
