using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Jobs;

namespace Tostatronic.Cnc.App;

/// <summary>Lógica de la ventana "PCB desde Gerber": cargar placa, validar, generar trayectorias,
/// guardar los .nc, mandarlos a la máquina y pasar el aislamiento al repaso de pistas.</summary>
public partial class CamViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private BoardModel? _board;
    private string? _sourcePath;

    public CamViewModel(MainViewModel main)
    {
        _main = main;
        Presets = new ObservableCollection<CamPreset>(CamPresetStore.Load());
        selectedPreset = Presets[0];
        travelXMm = main.Machine.Settings.TravelXMm;
        travelYMm = main.Machine.Settings.TravelYMm;
    }

    public ObservableCollection<CamPreset> Presets { get; }
    [ObservableProperty] private CamPreset? selectedPreset;
    [ObservableProperty] private string boardInfo = "Abre el ZIP de fabricación que exporta EasyEDA, Altium o KiCad (Gerber + barrenos), o una carpeta con los archivos sueltos.";
    [ObservableProperty] private string resultInfo = "";
    [ObservableProperty] private string warningsText = "";
    [ObservableProperty] private string infoText = "";
    [ObservableProperty] private int faceIndex; // 0 = inferior (espejo), 1 = superior
    [ObservableProperty] private int orientationIndex; // 0 = automática, 1 = sin girar, 2 = girada 90°
    [ObservableProperty] private double outlineMarginMm = 2.0;
    [ObservableProperty] private bool dropStrayCopper = true;
    [ObservableProperty] private double travelXMm;
    [ObservableProperty] private double travelYMm;
    [ObservableProperty] private string outputFolder = "";
    [ObservableProperty] private string? selectedFile;
    [ObservableProperty] private bool hasBoard;
    [ObservableProperty] private bool hasResult;

    public ObservableCollection<string> GeneratedFiles { get; } = new();

    public CamResult? Result { get; private set; }
    public event Action? ResultChanged;

    /// <summary>Pide abrir el repaso de pistas con el aislamiento generado (contenido, nombre, ruta ya guardada si la hay).</summary>
    public event Action<string, string, string?>? RepasoRequested;

    [RelayCommand]
    private void OpenZip()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir Gerber (ZIP de fabricación)",
            Filter = "ZIP de fabricación (*.zip)|*.zip|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        _sourcePath = dlg.FileName;
        LoadBoard(Path.GetDirectoryName(dlg.FileName)!);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta con los Gerber y los barrenos" };
        if (dlg.ShowDialog() != true) return;
        _sourcePath = dlg.FolderName;
        LoadBoard(dlg.FolderName);
    }

    private BoardLoadOptions LoadOptions => new()
    {
        DropStrayCopper = DropStrayCopper,
        OutlineMarginMm = OutlineMarginMm,
    };

    private void LoadBoard(string? folder)
    {
        if (_sourcePath == null) return;
        try
        {
            _board = BoardLoader.LoadPath(_sourcePath, LoadOptions);
            if (folder != null) OutputFolder = folder;
            HasBoard = true;
            // la cara que trae pistas; si la elegida no tiene cobre, la otra
            int preferred = _board.PreferredFace == BoardFace.Bottom ? 0 : 1;
            if (FaceIndex != preferred)
                FaceIndex = preferred; // dispara Generate()
            else
                Generate();
            BoardInfo = $"{_board.Name} · {_board.Source} · {_board.WidthMm:F1} × {_board.HeightMm:F1} mm" +
                        (_board.OutlineSynthetic ? " (cobre + margen)" : "") +
                        $" · {_board.Drills.Count} barrenos · " +
                        (_board.IsSingleLayer ? "UNA capa" : "DOS capas") + " · pistas en: " +
                        $"{(_board.BottomHasTraces ? "inferior " : "")}{(_board.TopHasTraces ? "superior" : "")}".Trim();
        }
        catch (Exception ex)
        {
            _board = null;
            HasBoard = false;
            HasResult = false;
            Result = null;
            BoardInfo = "No se pudo cargar: " + ex.Message;
            InfoText = "";
            WarningsText = "";
            ResultChanged?.Invoke();
        }
    }

    private BoardFace Face => FaceIndex == 0 ? BoardFace.Bottom : BoardFace.Top;

    partial void OnFaceIndexChanged(int value) => Generate();
    partial void OnOrientationIndexChanged(int value) => Generate();
    partial void OnOutlineMarginMmChanged(double value) => LoadBoard(null);
    partial void OnDropStrayCopperChanged(bool value) => LoadBoard(null);

    partial void OnTravelXMmChanged(double value) { SaveTravel(); Generate(); }
    partial void OnTravelYMmChanged(double value) { SaveTravel(); Generate(); }

    private void SaveTravel()
    {
        var s = _main.Machine.Settings;
        if (TravelXMm <= 0 || TravelYMm <= 0) return;
        if (Math.Abs(s.TravelXMm - TravelXMm) < 1e-9 && Math.Abs(s.TravelYMm - TravelYMm) < 1e-9) return;
        s.TravelXMm = TravelXMm;
        s.TravelYMm = TravelYMm;
        try { s.Save(); } catch (Exception) { /* sin permisos: se queda en memoria */ }
    }

    [RelayCommand]
    private void Generate()
    {
        if (_board == null || SelectedPreset == null) return;
        try
        {
            Result = CamGenerator.Generate(_board, Face, SelectedPreset, CurrentOptions);
            int rings = Result.Isolation.Count;
            double minutes = Result.IsolationLengthMm / Math.Max(1, SelectedPreset.FeedMmMin);
            ResultInfo = $"{Result.WidthMm:F1} × {Result.HeightMm:F1} mm sobre la CNC{(Result.Rotated ? " (girada 90°)" : "")} · " +
                         $"{rings} anillos de aislamiento, {Result.IsolationLengthMm / 1000:F2} m (~{minutes:F0} min) · " +
                         $"{Result.DrillCount} barrenos en {Result.DrillGroups.Count} broca(s) · " +
                         $"contorno {(Result.Contour.Count > 0 ? $"{Result.ContourDepths.Count} pasadas" : "—")} · {Result.Files.Count} archivos";
            WarningsText = string.Join("\n", Result.Warnings.Select(w => "⚠ " + w));
            InfoText = string.Join("\n", Result.Info.Select(i => "• " + i));
            HasResult = true;
        }
        catch (Exception ex)
        {
            Result = null;
            ResultInfo = "";
            InfoText = "";
            WarningsText = "⚠ " + ex.Message;
            HasResult = false;
        }
        GeneratedFiles.Clear();
        ResultChanged?.Invoke();
    }

    [RelayCommand]
    private void SaveFiles()
    {
        if (Result == null) return;
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            WarningsText = "⚠ Elige una carpeta de salida válida.";
            return;
        }
        GeneratedFiles.Clear();
        foreach (var (name, content) in Result.Files)
        {
            string path = Path.Combine(OutputFolder, name);
            File.WriteAllText(path, content);
            GeneratedFiles.Add(path);
        }
        // reporte de validación junto a los .nc
        string report = Path.Combine(OutputFolder, CamReport.FileNameFor(Result));
        File.WriteAllText(report, BuildReport());
        SelectedFile = GeneratedFiles.FirstOrDefault();
        ResultInfo = $"{GeneratedFiles.Count} archivos guardados en {OutputFolder} (más {Path.GetFileName(report)})";
    }

    /// <summary>Texto del reporte de validación: lo mismo que se ve en pantalla, para dejarlo junto a los .nc.</summary>
    public string BuildReport() =>
        Result == null || SelectedPreset == null ? "" : CamReport.Build(Result, SelectedPreset, CurrentOptions, _sourcePath);

    private CamOptions CurrentOptions => new()
    {
        Rotation = OrientationIndex switch { 1 => RotationMode.None, 2 => RotationMode.Rotate90, _ => RotationMode.Auto },
        TravelXMm = TravelXMm > 0 ? TravelXMm : 100,
        TravelYMm = TravelYMm > 0 ? TravelYMm : 140,
    };

    [RelayCommand]
    private void PickOutputFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta de salida para los .nc" };
        if (dlg.ShowDialog() == true)
            OutputFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void LoadIntoMachine()
    {
        if (SelectedFile != null)
            _main.LoadProgramFromPath(SelectedFile);
    }

    [RelayCommand]
    private void OpenRepaso()
    {
        if (Result == null) return;
        var (name, content) = Result.Files[0]; // el aislamiento siempre va primero
        string? saved = GeneratedFiles.FirstOrDefault(f => Path.GetFileName(f) == name);
        RepasoRequested?.Invoke(content, name, saved ?? (Directory.Exists(OutputFolder) ? Path.Combine(OutputFolder, name) : null));
    }

    [RelayCommand]
    private void SavePresets()
    {
        CamPresetStore.Save(Presets.ToList());
        ResultInfo = "Presets guardados.";
    }
}
