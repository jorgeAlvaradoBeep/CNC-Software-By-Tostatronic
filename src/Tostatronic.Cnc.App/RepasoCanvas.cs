using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clipper2Lib;
using Tostatronic.Cnc.Cam.Jobs;

namespace Tostatronic.Cnc.App;

/// <summary>
/// Lienzo del repaso de pistas: dibuja las trayectorias de un .nc (y un fondo opcional), deja
/// marcarlas con clic, dibujar zonas con Shift + arrastrar, marcar una sección de una trayectoria
/// con Ctrl + arrastrar a lo largo de ella, mover con arrastrar y hacer zoom con la rueda.
/// Coordenadas en mm de trabajo, Y hacia arriba como en la CNC.
/// </summary>
public sealed class RepasoCanvas : FrameworkElement
{
    private RepasoProgram? _program;
    private RepasoProgram? _background;
    private readonly HashSet<int> _selected = new();
    private readonly List<RectMm> _zones = new();
    private readonly List<RepasoSection> _sections = new();
    private readonly List<List<PointD>> _sectionPts = new();   // puntos de cada sección, para dibujar y elegir
    private int _hover = -1;
    private int _hoverSection = -1;

    private double _scale = 4;   // px por mm
    private double _ox = 40;     // origen (0,0) en px
    private double _oy = 40;

    private Point? _dragStart;
    private Point _dragCur;
    private bool _dragZone;
    private bool _dragMoved;
    private double _dragOx, _dragOy;
    private int _dragSectionPath = -1;                 // Ctrl + arrastrar: trayectoria sobre la que se marca la sección
    private readonly List<double> _dragSamples = new(); // parámetros por donde pasó el ratón, para saber el sentido
    private (double From, double To)? _dragSection;    // sección en curso (vista previa)

    private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2E)), 1));
    private static readonly Pen AxisPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4F, 0x57)), 1));
    private static readonly Pen BgPathPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x45, 0x4A, 0x52)), 1));
    private static readonly Brush ZoneFill = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x53, 0xD5, 0x8A)));
    private static readonly Pen ZonePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x53, 0xD5, 0x8A)), 1.5));
    private static readonly Pen ZoneDragPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x53, 0xD5, 0x8A)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Color PathColor = Color.FromRgb(0x4F, 0x8F, 0xD8);
    private static readonly Color SelColor = Color.FromRgb(0xFF, 0xA6, 0x3D);
    private static readonly Color SectionColor = Color.FromRgb(0xE8, 0x5D, 0xC8);
    private static readonly Brush SectionEndBrush = Freeze(new SolidColorBrush(SectionColor));
    private static readonly Brush SectionDragBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0xE8, 0x5D, 0xC8)));
    private static readonly Brush NumBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8D, 0x93, 0xA0)));
    private static readonly Brush NumSelBrush = Freeze(new SolidColorBrush(SelColor));
    private static readonly Typeface Mono = new("Consolas");

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public RepasoCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public RepasoProgram? Program
    {
        get => _program;
        set { _program = value; _selected.Clear(); _zones.Clear(); _sections.Clear(); _sectionPts.Clear(); _hover = -1; _hoverSection = -1; InvalidateVisual(); }
    }

    public RepasoProgram? Background
    {
        get => _background;
        set { _background = value; InvalidateVisual(); }
    }

    public IReadOnlySet<int> Selected => _selected;
    public IReadOnlyList<RectMm> Zones => _zones;
    public IReadOnlyList<RepasoSection> Sections => _sections;

    /// <summary>Se dispara al cambiar la selección o las zonas.</summary>
    public event Action? SelectionChanged;

    /// <summary>Texto de estado (posición del ratón, trayectoria bajo el cursor).</summary>
    public event Action<string>? StatusChanged;

    public void SetSelection(IEnumerable<int> selected, IEnumerable<RectMm> zones) =>
        SetSelection(selected, zones, Array.Empty<RepasoSection>());

    public void SetSelection(IEnumerable<int> selected, IEnumerable<RectMm> zones, IEnumerable<RepasoSection> sections)
    {
        _selected.Clear();
        _zones.Clear();
        _sections.Clear();
        if (_program != null)
        {
            foreach (int i in selected)
                if (i >= 0 && i < _program.Paths.Count) _selected.Add(i);
            foreach (var s in sections)
                if (s.Path >= 0 && s.Path < _program.Paths.Count) _sections.Add(s);
        }
        _zones.AddRange(zones);
        RebuildSections();
        InvalidateVisual();
    }

    public void ClearSections()
    {
        _sections.Clear();
        RebuildSections();
        Changed();
    }

    private void RebuildSections()
    {
        _sectionPts.Clear();
        _hoverSection = -1;
        if (_program == null) return;
        foreach (var s in _sections) _sectionPts.Add(_program.Paths[s.Path].Slice(s.From, s.To));
    }

    public void SelectAll()
    {
        if (_program == null) return;
        for (int i = 0; i < _program.Paths.Count; i++) _selected.Add(i);
        Changed();
    }

    public void SelectNone()
    {
        _selected.Clear();
        _zones.Clear();
        _sections.Clear();
        RebuildSections();
        Changed();
    }

    public void Invert()
    {
        if (_program == null) return;
        for (int i = 0; i < _program.Paths.Count; i++)
            if (!_selected.Remove(i)) _selected.Add(i);
        Changed();
    }

    public void ClearZones()
    {
        _zones.Clear();
        Changed();
    }

    private void Changed()
    {
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    // ---- vista ----

    private Point ToScreen(PointD p) => new(_ox + p.x * _scale, _oy - p.y * _scale);
    private PointD ToWorld(Point s) => new((s.X - _ox) / _scale, (_oy - s.Y) / _scale);

    public void Fit()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        var rects = new List<RectMm>();
        if (_program != null && _program.Paths.Count > 0) rects.Add(_program.Bounds);
        if (_background != null && _background.Paths.Count > 0) rects.Add(_background.Bounds);
        if (rects.Count == 0) return;
        double x0 = rects.Min(r => r.X0), y0 = rects.Min(r => r.Y0), x1 = rects.Max(r => r.X1), y1 = rects.Max(r => r.Y1);
        const double m = 30;
        _scale = Math.Min((ActualWidth - 2 * m) / Math.Max(1e-6, x1 - x0), (ActualHeight - 2 * m) / Math.Max(1e-6, y1 - y0));
        _ox = (ActualWidth - (x0 + x1) * _scale) / 2;
        _oy = (ActualHeight + (y0 + y1) * _scale) / 2;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width < 10 && _program != null) Fit();
    }

    // ---- dibujo ----

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

        // rejilla cada 10 mm y ejes
        var wTopLeft = ToWorld(new Point(0, 0));
        var wBottomRight = ToWorld(new Point(w, h));
        for (double x = Math.Floor(wTopLeft.x / 10) * 10; x <= wBottomRight.x; x += 10)
        {
            double sx = Math.Round(_ox + x * _scale) + 0.5;
            dc.DrawLine(GridPen, new Point(sx, 0), new Point(sx, h));
        }
        for (double y = Math.Floor(wBottomRight.y / 10) * 10; y <= wTopLeft.y; y += 10)
        {
            double sy = Math.Round(_oy - y * _scale) + 0.5;
            dc.DrawLine(GridPen, new Point(0, sy), new Point(w, sy));
        }
        dc.DrawLine(AxisPen, new Point(Math.Round(_ox) + 0.5, 0), new Point(Math.Round(_ox) + 0.5, h));
        dc.DrawLine(AxisPen, new Point(0, Math.Round(_oy) + 0.5), new Point(w, Math.Round(_oy) + 0.5));

        if (_background != null)
            dc.DrawGeometry(null, BgPathPen, Build(_background.Paths.Select(p => p.Points)));

        if (_program == null) return;
        var paths = _program.Paths;

        var normal = new List<List<PointD>>();
        var sel = new List<List<PointD>>();
        for (int i = 0; i < paths.Count; i++)
        {
            if (i == _hover) continue;
            (_selected.Contains(i) ? sel : normal).Add(paths[i].Points);
        }
        dc.DrawGeometry(null, MakePen(PathColor, Math.Max(1, 0.25 * _scale)), Build(normal));
        dc.DrawGeometry(null, MakePen(SelColor, Math.Max(2.5, 0.3 * _scale)), Build(sel));
        if (_hover >= 0 && _hover < paths.Count)
            dc.DrawGeometry(null, MakePen(Colors.White, Math.Max(3, 0.35 * _scale)), Build(new[] { paths[_hover].Points }));

        // secciones (solo un tramo de una trayectoria), con sus extremos
        if (_sectionPts.Count > 0)
        {
            dc.DrawGeometry(null, MakePen(SectionColor, Math.Max(3, 0.35 * _scale)),
                Build(_sectionPts.Where((_, i) => i != _hoverSection)));
            if (_hoverSection >= 0 && _hoverSection < _sectionPts.Count)
                dc.DrawGeometry(null, MakePen(Colors.White, Math.Max(3.5, 0.4 * _scale)), Build(new[] { _sectionPts[_hoverSection] }));
            foreach (var pts in _sectionPts)
            {
                if (pts.Count < 2) continue;
                dc.DrawEllipse(SectionEndBrush, null, ToScreen(pts[0]), 3.5, 3.5);
                dc.DrawEllipse(SectionEndBrush, null, ToScreen(pts[^1]), 3.5, 3.5);
            }
        }
        if (_dragSection is { } dsec && _dragSectionPath >= 0 && _dragSectionPath < paths.Count)
        {
            var pts = paths[_dragSectionPath].Slice(dsec.From, dsec.To);
            if (pts.Count >= 2)
            {
                dc.DrawGeometry(null, Freeze(new Pen(SectionDragBrush, Math.Max(4, 0.45 * _scale)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }), Build(new[] { pts }));
                dc.DrawEllipse(SectionEndBrush, null, ToScreen(pts[0]), 4, 4);
                dc.DrawEllipse(SectionEndBrush, null, ToScreen(pts[^1]), 4, 4);
            }
        }

        // número al inicio de cada trayectoria
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < paths.Count; i++)
        {
            var s = ToScreen(paths[i].Points[0]);
            if (s.X < -20 || s.Y < -20 || s.X > w + 20 || s.Y > h + 20) continue;
            var ft = new FormattedText((i + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 11, _selected.Contains(i) ? NumSelBrush : NumBrush, dip);
            dc.DrawText(ft, new Point(s.X + 3, s.Y - 2 - ft.Height));
        }

        // zonas
        foreach (var z in _zones)
        {
            var a = ToScreen(new PointD(z.X0, z.Y1));
            var b = ToScreen(new PointD(z.X1, z.Y0));
            dc.DrawRectangle(ZoneFill, ZonePen, new Rect(a, b));
        }
        if (_dragStart is { } ds && _dragZone)
            dc.DrawRectangle(null, ZoneDragPen, new Rect(ds, _dragCur));
    }

    private static Pen MakePen(Color c, double width) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(c)), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private StreamGeometry Build(IEnumerable<IReadOnlyList<PointD>> polylines)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var pts in polylines)
            {
                if (pts.Count < 2) continue;
                ctx.BeginFigure(ToScreen(pts[0]), false, false);
                for (int i = 1; i < pts.Count; i++)
                    ctx.LineTo(ToScreen(pts[i]), true, true);
            }
        }
        g.Freeze();
        return g;
    }

    // ---- interacción ----

    private int Pick(Point s)
    {
        if (_program == null) return -1;
        var wpt = ToWorld(s);
        return _program.Pick(wpt.x, wpt.y, 9 / _scale); // 9 px de tolerancia
    }

    /// <summary>Índice de la sección más cercana al punto de pantalla, o −1.</summary>
    private int PickSection(Point s)
    {
        if (_program == null) return -1;
        var w = ToWorld(s);
        int best = -1;
        double bestD = 9 / _scale;
        for (int i = 0; i < _sectionPts.Count; i++)
        {
            var pts = _sectionPts[i];
            for (int k = 1; k < pts.Count; k++)
            {
                var a = pts[k - 1]; var b = pts[k];
                double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Clamp(((w.x - a.x) * dx + (w.y - a.y) * dy) / l2, 0, 1) : 0;
                double d = double.Hypot(w.x - (a.x + t * dx), w.y - (a.y + t * dy));
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        return best;
    }

    /// <summary>Sección en curso según el inicio del arrastre, la posición actual y por dónde pasó el ratón.</summary>
    private (double From, double To)? CurrentDragSection(Point cur)
    {
        if (_program == null || _dragSectionPath < 0 || _dragStart is not { } ds) return null;
        var path = _program.Paths[_dragSectionPath];
        var a = ToWorld(ds); var b = ToWorld(cur);
        double ua = path.Project(a.x, a.y).U, ub = path.Project(b.x, b.y).U;
        if (Math.Abs(ua - ub) < 1e-9) return null;
        return path.ChooseArc(ua, ub, _dragSamples);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;
        Focus();
        CaptureMouse();
        _dragStart = _dragCur = e.GetPosition(this);
        _dragZone = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _dragMoved = false;
        _dragOx = _ox; _dragOy = _oy;
        _dragSectionPath = -1;
        _dragSection = null;
        _dragSamples.Clear();
        if (!_dragZone && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl + arrastrar sobre una trayectoria: sección. Sin trayectoria debajo, se mueve la vista.
            _dragSectionPath = Pick(_dragStart.Value);
            if (_dragSectionPath >= 0 && _program != null)
            {
                var w = ToWorld(_dragStart.Value);
                _dragSamples.Add(_program.Paths[_dragSectionPath].Project(w.x, w.y).U);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var s = e.GetPosition(this);
        if (_dragStart is { } ds)
        {
            _dragCur = s;
            if ((s - ds).Length > 4) _dragMoved = true;
            if (_dragSectionPath >= 0 && _program != null)
            {
                var w = ToWorld(s);
                _dragSamples.Add(_program.Paths[_dragSectionPath].Project(w.x, w.y).U);
                _dragSection = _dragMoved ? CurrentDragSection(s) : null;
            }
            else if (!_dragZone && _dragMoved)
            {
                _ox = _dragOx + (s.X - ds.X);
                _oy = _dragOy + (s.Y - ds.Y);
            }
            InvalidateVisual();
        }
        else
        {
            int h = -1, hs = -1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) hs = PickSection(s);
            if (hs < 0) h = Pick(s);
            if (h != _hover || hs != _hoverSection) { _hover = h; _hoverSection = hs; InvalidateVisual(); }
        }
        var wpt = ToWorld(s);
        string hov = "";
        if (_program != null)
        {
            if (_dragSection is { } dsec && _dragSectionPath >= 0)
                hov = $"   [sección de {_dragSectionPath + 1}: {_program.Paths[_dragSectionPath].SliceLengthMm(dsec.From, dsec.To):F0} mm]";
            else if (_hoverSection >= 0 && _hoverSection < _sections.Count)
            {
                var sec = _sections[_hoverSection];
                hov = $"   [sección de {sec.Path + 1}: {_program.Paths[sec.Path].SliceLengthMm(sec.From, sec.To):F0} mm · Ctrl + clic la quita]";
            }
            else if (_hover >= 0)
                hov = $"   [{_hover + 1}: {_program.Paths[_hover].Points.Count} pts, {_program.Paths[_hover].LengthMm:F0} mm]";
        }
        StatusChanged?.Invoke($"X{wpt.x:F2} Y{wpt.y:F2}{hov}");
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left || _dragStart is not { } ds) return;
        ReleaseMouseCapture();
        var s = e.GetPosition(this);
        if (_dragSectionPath >= 0 && _program != null)
        {
            // Ctrl + arrastrar a lo largo de una trayectoria: queda solo ese tramo
            var sec = _dragMoved ? CurrentDragSection(s) : null;
            int pathIdx = _dragSectionPath;
            _dragStart = null; _dragSectionPath = -1; _dragSection = null; _dragSamples.Clear();
            if (sec is { } r && _program.Paths[pathIdx].SliceLengthMm(r.From, r.To) > 0.05)
            {
                _selected.Remove(pathIdx);   // la pista deja de estar marcada completa: se reduce a la sección
                _sections.Add(new RepasoSection(pathIdx, r.From, r.To));
                RebuildSections();
                Changed();
            }
            else if (!_dragMoved)
            {
                // Ctrl + clic sobre una sección: quitarla
                int k = PickSection(s);
                if (k >= 0) { _sections.RemoveAt(k); RebuildSections(); Changed(); }
                else InvalidateVisual();
            }
            else InvalidateVisual();
            return;
        }
        _dragStart = null;
        _dragSectionPath = -1; _dragSection = null; _dragSamples.Clear();
        if (_dragZone && _dragMoved)
        {
            var a = ToWorld(ds);
            var b = ToWorld(s);
            _zones.Add(RectMm.FromCorners(a.x, a.y, b.x, b.y));
            Changed();
        }
        else if (_dragZone)
        {
            var wpt = ToWorld(s);
            int k = _zones.FindIndex(z => z.Contains(wpt.x, wpt.y));
            if (k >= 0) { _zones.RemoveAt(k); Changed(); }
        }
        else if (!_dragMoved)
        {
            int i = Pick(s);
            if (i >= 0)
            {
                if (!_selected.Remove(i)) _selected.Add(i);
                Changed();
            }
        }
        else
        {
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1 || _hoverSection != -1) { _hover = -1; _hoverSection = -1; InvalidateVisual(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var s = e.GetPosition(this);
        var wpt = ToWorld(s);
        double k = e.Delta > 0 ? 1.2 : 1 / 1.2;
        _scale = Math.Clamp(_scale * k, 0.2, 400);
        _ox = s.X - wpt.x * _scale;
        _oy = s.Y + wpt.y * _scale;
        InvalidateVisual();
        e.Handled = true;
    }
}
