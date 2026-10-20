using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Clipper2Lib;

namespace Tostatronic.Cnc.App;

public partial class CamWindow : Window
{
    private readonly CamViewModel _vm;

    public CamWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new CamViewModel(main);
        DataContext = _vm;
        _vm.ResultChanged += () => Dispatcher.BeginInvoke(RenderPreview);
    }

    private const double Pad = 4.0; // mm alrededor de la placa

    private void RenderPreview()
    {
        PreviewCanvas.Children.Clear();
        var r = _vm.Result;
        if (r == null)
        {
            PreviewCanvas.Width = PreviewCanvas.Height = 0;
            return;
        }

        double w = r.WidthMm, h = r.HeightMm;
        PreviewCanvas.Width = w + 2 * Pad;
        PreviewCanvas.Height = h + 2 * Pad;
        double X(double x) => x + Pad;
        double Y(double y) => h + Pad - y; // la Y del lienzo crece hacia abajo

        // placa
        var board = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x1E)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x36)),
            StrokeThickness = 0.1,
        };
        System.Windows.Controls.Canvas.SetLeft(board, X(0));
        System.Windows.Controls.Canvas.SetTop(board, Y(h));
        PreviewCanvas.Children.Add(board);

        // cobre (anillos con hoyos, regla par-impar)
        var copperGeo = new StreamGeometry { FillRule = System.Windows.Media.FillRule.EvenOdd };
        using (var ctx = copperGeo.Open())
        {
            foreach (var path in r.Copper)
            {
                if (path.Count < 3) continue;
                ctx.BeginFigure(new Point(X(path[0].x), Y(path[0].y)), true, true);
                for (int i = 1; i < path.Count; i++)
                    ctx.LineTo(new Point(X(path[i].x), Y(path[i].y)), false, false);
            }
        }
        copperGeo.Freeze();
        PreviewCanvas.Children.Add(new Path
        {
            Data = copperGeo,
            Fill = new SolidColorBrush(Color.FromRgb(0xC2, 0x84, 0x3C)),
        });

        // aislamiento: al ancho real de la herramienta
        double tool = Math.Max(0.05, _vm.SelectedPreset?.ToolMm ?? 0.2);
        var isoGeo = new StreamGeometry();
        using (var ctx = isoGeo.Open())
        {
            foreach (var ring in r.Isolation)
            {
                if (ring.Count < 2) continue;
                ctx.BeginFigure(new Point(X(ring[0].x), Y(ring[0].y)), false, true);
                for (int i = 1; i < ring.Count; i++)
                    ctx.LineTo(new Point(X(ring[i].x), Y(ring[i].y)), true, true);
            }
        }
        isoGeo.Freeze();
        PreviewCanvas.Children.Add(new Path
        {
            Data = isoGeo,
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x4F, 0xC3, 0xF7)),
            StrokeThickness = tool,
            StrokeLineJoin = PenLineJoin.Round,
        });

        // contorno: al ancho de la fresa
        if (r.Contour.Count >= 2)
        {
            var contourGeo = new StreamGeometry();
            using (var ctx = contourGeo.Open())
            {
                ctx.BeginFigure(new Point(X(r.Contour[0].x), Y(r.Contour[0].y)), false, true);
                for (int i = 1; i < r.Contour.Count; i++)
                    ctx.LineTo(new Point(X(r.Contour[i].x), Y(r.Contour[i].y)), true, true);
            }
            contourGeo.Freeze();
            PreviewCanvas.Children.Add(new Path
            {
                Data = contourGeo,
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xE5, 0x39, 0x35)),
                StrokeThickness = Math.Max(0.2, _vm.SelectedPreset?.ContourToolMm ?? 1.0),
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        // barrenos
        var drillGeo = new GeometryGroup();
        foreach (var (bit, pts) in r.DrillGroups)
            foreach (var p in pts)
                drillGeo.Children.Add(new EllipseGeometry(new Point(X(p.x), Y(p.y)), bit / 2, bit / 2));
        drillGeo.Freeze();
        PreviewCanvas.Children.Add(new Path
        {
            Data = drillGeo,
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0x9B, 0xA6)),
            StrokeThickness = 0.06,
        });

        // origen X0 Y0
        var cross = new GeometryGroup();
        cross.Children.Add(new LineGeometry(new Point(X(-2), Y(0)), new Point(X(2), Y(0))));
        cross.Children.Add(new LineGeometry(new Point(X(0), Y(-2)), new Point(X(0), Y(2))));
        cross.Freeze();
        PreviewCanvas.Children.Add(new Path
        {
            Data = cross,
            Stroke = Brushes.White,
            StrokeThickness = 0.15,
        });
    }
}


