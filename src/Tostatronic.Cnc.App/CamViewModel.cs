using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Tostatronic.Cnc.Cam.Board;
using Tostatronic.Cnc.Cam.Jobs;

namespace Tostatronic.Cnc.App;

/// <summary>Lógica de la ventana "PCB desde Gerber": cargar placa, generar trayectorias,
/// guardar los .nc y mandarlos a la máquina.</summary>
public partial class CamViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private BoardModel? _board;

    public CamViewModel(MainViewModel main)
    {
        _main = main;
        Presets = new ObservableCollection<CamPreset>(CamPresetStore.Load());
        selectedPreset = Presets[0];
    }

    public ObservableCollection<CamPreset> Presets { get; }
    [ObservableProperty] private CamPreset? selectedPreset;
    [ObservableProperty] private string boardInfo = "Abre el ZIP de fabricación que exporta EasyEDA (Gerber + Excellon), o una carpeta con los archivos sueltos.";
    [ObservableProperty] private string resultInfo = "";
    [ObservableProperty] private string warningsText = "";
    [ObservableProperty] private int faceIndex; // 0 = inferior (espejo), 1 = superior
    [ObservableProperty] private string outputFolder = "";
    [ObservableProperty] private string? selectedFile;
    [ObservableProperty] private bool hasBoard;

    public ObservableCollection<string> GeneratedFiles { get; } = new();

    public CamResult? Result { get; private set; }
    public event Action? ResultChanged;

    [RelayCommand]
    private void OpenZip()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir Gerber de EasyEDA",
            Filter = "ZIP de fabricación (*.zip)|*.zip|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        LoadBoard(() => BoardLoader.LoadZip(dlg.FileName), Path.GetDirectoryName(dlg.FileName)!);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta con los Gerber y el Excellon" };
        if (dlg.ShowDialog() != true) return;
        LoadBoard(() => BoardLoader.LoadFolder(dlg.FolderName), dlg.FolderName);
    }

    private void LoadBoard(Func<BoardModel> load, string folder)
    {
        try
        {
            _board = load();
            OutputFolder = folder;
            HasBoard = true;
            // si la cara elegida no trae cobre, cambiar a la que sí
            if (!_board.HasFace(Face) && _board.HasFace(Other(Face)))
                FaceIndex = 1 - FaceIndex;
            BoardInfo = $"{_board.Name} · {_board.WidthMm:F1} × {_board.HeightMm:F1} mm · " +
                        $"{_board.Drills.Count} barrenos · cobre: " +
                        $"{(_board.CopperBottom != null ? "inferior " : "")}{(_board.CopperTop != null ? "superior" : "")}".Trim();
            Generate();
        }
        catch (Exception ex)
        {
            _board = null;
            HasBoard = false;
            Result = null;
            BoardInfo = "No se pudo cargar: " + ex.Message;
            ResultChanged?.Invoke();
        }
    }

    private BoardFace Face => FaceIndex == 0 ? BoardFace.Bottom : BoardFace.Top;
    private static BoardFace Other(BoardFace f) => f == BoardFace.Bottom ? BoardFace.Top : BoardFace.Bottom;

    partial void OnFaceIndexChanged(int value) => Generate();

    [RelayCommand]
    private void Generate()
    {
        if (_board == null || SelectedPreset == null) return;
        try
        {
            Result = CamGenerator.Generate(_board, Face, SelectedPreset);
            int rings = Result.Isolation.Count;
            ResultInfo = $"{rings} anillos de aislamiento · {Result.DrillCount} barrenos en {Result.DrillGroups.Count} broca(s) · " +
                         $"contorno {(Result.Contour.Count > 0 ? $"{Result.ContourDepths.Count} pasadas" : "—")} · {Result.Files.Count} archivos";
            WarningsText = string.Join("\n", Result.Warnings.Select(w => "⚠ " + w));
        }
        catch (Exception ex)
        {
            Result = null;
            ResultInfo = "";
            WarningsText = "⚠ " + ex.Message;
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
        SelectedFile = GeneratedFiles.FirstOrDefault();
        ResultInfo = $"{GeneratedFiles.Count} archivos guardados en {OutputFolder}";
    }

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
    private void SavePresets()
    {
        CamPresetStore.Save(Presets.ToList());
        ResultInfo = "Presets guardados.";
    }
}
