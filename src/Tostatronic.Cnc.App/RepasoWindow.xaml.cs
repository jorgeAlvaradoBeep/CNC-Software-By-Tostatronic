using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Tostatronic.Cnc.Cam.Jobs;

namespace Tostatronic.Cnc.App;

/// <summary>
/// Ventana "Repaso de pistas": carga un .nc de aislamiento, deja marcar trayectorias, secciones
/// de trayectoria y zonas sobre el dibujo y exporta (o manda a la máquina) un .nc solo con eso, a la profundidad que
/// se pida. La selección se guarda junto al archivo (.repaso.json) para retomarla.
/// </summary>
public partial class RepasoWindow : Window
{
    private readonly MainViewModel _main;
    private RepasoProgram? _program;
    private string? _ncPath;        // ruta del .nc cargado (o donde se guardaría), para la selección y la exportación
    private bool _loading;

    public RepasoWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Canvas.SelectionChanged += OnSelectionChanged;
        Canvas.StatusChanged += s => StatusText.Text = s;
    }

    // ---- carga ----

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir .nc de aislamiento",
            Filter = "G-code (*.nc;*.gcode;*.txt)|*.nc;*.gcode;*.txt|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            LoadFromText(File.ReadAllText(dlg.FileName), Path.GetFileName(dlg.FileName), dlg.FileName);
        }
        catch (Exception ex)
        {
            FileText.Text = "No se pudo cargar: " + ex.Message;
        }
    }

    /// <summary>Carga un .nc ya leído (lo usa la ventana de Gerber con el aislamiento recién generado).</summary>
    public void LoadFromText(string content, string fileName, string? path)
    {
        try
        {
            _program = RepasoProgram.Parse(content, fileName);
        }
        catch (Exception ex)
        {
            _program = null;
            Canvas.Program = null;
            FileText.Text = "No se pudo cargar: " + ex.Message;
            return;
        }
        _ncPath = path;
        _loading = true;
        Canvas.Program = _program;
        DepthBox.Text = _program.DepthMm.ToString("0.0##", CultureInfo.InvariantCulture);
        DepthHint.Text = $"(archivo: {_program.DepthMm.ToString("0.0##", CultureInfo.InvariantCulture)} mm)";
        var b = _program.Bounds;
        FileText.Text = $"{fileName}: {_program.Paths.Count} trayectorias, {_program.TotalLengthMm / 1000:F2} m · " +
                        $"X {b.X0:F1}…{b.X1:F1} · Y {b.Y0:F1}…{b.Y1:F1} · Z segura {_program.SafeZMm:0.##}, F{_program.FeedMmMin}/{_program.PlungeMmMin}" +
                        (path == null ? " · (sin ruta: la selección no se guarda)" : "");
        // selección guardada
        if (path != null && RepasoSelection.Load(path, _program.Paths.Count) is { } saved)
        {
            Canvas.SetSelection(saved.Selected, saved.ZoneRects(), saved.SectionsFor(_program));
            if (saved.DepthMm > 0) DepthBox.Text = saved.DepthMm.ToString("0.0##", CultureInfo.InvariantCulture);
        }
        _loading = false;
        ExportText.Text = "";
        Canvas.Fit();
        OnSelectionChanged();
    }

    private void OpenBackground_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir .nc de fondo (solo para ubicarse)",
            Filter = "G-code (*.nc;*.gcode;*.txt)|*.nc;*.gcode;*.txt|Todos (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Canvas.Background = RepasoProgram.Load(dlg.FileName);
            Canvas.Fit();
        }
        catch (Exception ex)
        {
            FileText.Text = "Fondo: " + ex.Message;
        }
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e) => Canvas.Background = null;

    // ---- selección ----

    private void All_Click(object sender, RoutedEventArgs e) => Canvas.SelectAll();
    private void None_Click(object sender, RoutedEventArgs e) => Canvas.SelectNone();
    private void Invert_Click(object sender, RoutedEventArgs e) => Canvas.Invert();
    private void ClearZones_Click(object sender, RoutedEventArgs e) => Canvas.ClearZones();
    private void ClearSections_Click(object sender, RoutedEventArgs e) => Canvas.ClearSections();
    private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void Depth_Changed(object sender, RoutedEventArgs e) => SaveSelection();

    private double Depth =>
        double.TryParse(DepthBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0
            ? d
            : _program?.DepthMm ?? 0.12;

    private void OnSelectionChanged()
    {
        if (_program == null) return;
        double mm = Canvas.Selected.Sum(i => _program.Paths[i].LengthMm);
        double secMm = Canvas.Sections.Sum(s => _program.Paths[s.Path].SliceLengthMm(s.From, s.To));
        SelText.Text = $"{Canvas.Selected.Count} de {_program.Paths.Count} trayectorias marcadas ({mm:F0} mm) · " +
                       $"{Canvas.Sections.Count} secciones ({secMm:F0} mm) · {Canvas.Zones.Count} zonas";
        SaveSelection();
    }

    private void SaveSelection()
    {
        if (_program == null || _ncPath == null || _loading) return;
        try
        {
            new RepasoSelection
            {
                Selected = Canvas.Selected.OrderBy(i => i).ToList(),
                Zones = Canvas.Zones.Select(z => new[] { z.X0, z.Y0, z.X1, z.Y1 }).ToList(),
                Sections = Canvas.Sections.ToList(),
                DepthMm = Depth,
                PathCount = _program.Paths.Count,
            }.Save(_ncPath);
        }
        catch (Exception)
        {
            // carpeta de solo lectura: la selección vive solo en esta sesión
        }
    }

    // ---- exportar ----

    private string? Export()
    {
        if (_program == null)
        {
            ExportText.Text = "Primero carga un archivo .nc.";
            return null;
        }
        RepasoExport result;
        try
        {
            result = _program.Export(Canvas.Selected, Canvas.Zones, Canvas.Sections, Depth);
        }
        catch (Exception ex)
        {
            ExportText.Text = ex.Message;
            return null;
        }
        string suggested = RepasoProgram.SuggestedFileName(_program.Name);
        string? path;
        if (_ncPath != null && Directory.Exists(Path.GetDirectoryName(_ncPath)))
        {
            path = Path.Combine(Path.GetDirectoryName(_ncPath)!, suggested);
        }
        else
        {
            var dlg = new SaveFileDialog { Title = "Guardar repaso", FileName = suggested, Filter = "G-code (*.nc)|*.nc" };
            if (dlg.ShowDialog() != true) return null;
            path = dlg.FileName;
        }
        File.WriteAllText(path, result.Content);
        ExportText.Text = $"Guardado {Path.GetFileName(path)}: {result.Pieces} tramos, {result.LengthMm:F0} mm de corte " +
                          $"(~{Math.Ceiling(result.LengthMm / Math.Max(1, _program.FeedMmMin))} min) a {result.DepthMm:0.0##} mm.\n{path}";
        return path;
    }

    private void Export_Click(object sender, RoutedEventArgs e) => Export();

    private void ExportAndLoad_Click(object sender, RoutedEventArgs e)
    {
        var path = Export();
        if (path != null)
            _main.LoadProgramFromPath(path);
    }
}
