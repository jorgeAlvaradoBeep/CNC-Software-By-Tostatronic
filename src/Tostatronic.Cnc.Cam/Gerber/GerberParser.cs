using Clipper2Lib;
using Tostatronic.Cnc.Cam.Geometry;

namespace Tostatronic.Cnc.Cam.Gerber;

/// <summary>Resultado de parsear una capa Gerber: el relleno (cobre/contorno) ya unido.</summary>
public sealed class GerberImage
{
    /// <summary>PolÃ­gonos rellenos (unidades de Clipper), con polaridad aplicada.</summary>
    public Paths64 Filled { get; internal set; } = new();

    /// <summary>Trazos crudos (segmentos en mm), para encadenar contornos de placa.</summary>
    public List<(PointD A, PointD B)> Segments { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Parser de Gerber RS-274X (el formato de fabricaciÃ³n que exporta EasyEDA y cualquier CAD):
/// formato FS, unidades MO, aperturas estÃ¡ndar C/R/O/P y macros AM (primitivas 1, 4, 5, 7,
/// 20 y 21, con variables y aritmÃ©tica), trazos G01, arcos G02/G03 (G75 y aproximaciÃ³n G74),
/// flashes D03, regiones G36/G37 y polaridad LPD/LPC. Lo que no conoce lo reporta en
/// Warnings en lugar de fallar.
/// </summary>
public sealed class GerberParser
{
    private readonly GerberImage _img = new();
    private readonly Dictionary<int, Aperture> _apertures = new();
    private readonly Dictionary<string, List<string>> _macros = new();
    private readonly HashSet<string> _warned = new();

    private int _decimals = 4;
    private int _intDigits = 3;
    private bool _trailingOmitted;       // TZ: se rellena a la derecha
    private double _unit = 1.0;          // 1 = mm, 25.4 = pulgadas
    private int _interp = 1;             // 1 lineal, 2 horario, 3 antihorario
    private bool _multiQuadrant = true;
    private bool _dark = true;           // LPD / LPC
    private bool _inRegion;
    private Aperture? _current;
    private long _x, _y;
    private bool _hasPos;

    private Paths64 _batch = new();      // geometrÃ­a pendiente con la polaridad actual
    private Path64 _contour = new();     // contorno de regiÃ³n en construcciÃ³n

    private string? _pendingMacro;       // macro AM cuyo cuerpo sigue llegando

    private sealed record Aperture(char Kind, double[] Args, string? MacroName)
    {
        public double W => Args.Length > 0 ? Args[0] : 0;
        public double H => Args.Length > 1 ? Args[1] : W;
    }

    public static GerberImage Parse(string text)
    {
        var p = new GerberParser();
        p.Run(text);
        return p._img;
    }

    private void Warn(string msg)
    {
        if (_warned.Add(msg))
            _img.Warnings.Add(msg);
    }

    private void Run(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '%')
            {
                int end = text.IndexOf('%', i + 1);
                if (end < 0) break;
                var inner = text[(i + 1)..end];
                bool first = true;
                foreach (var cmd in inner.Split('*', StringSplitOptions.RemoveEmptyEntries))
                {
                    Extended(cmd.Trim(), first);
                    first = false;
                }
                _pendingMacro = null; // una macro termina con su %
                i = end + 1;
            }
            else
            {
                int end = text.IndexOf('*', i);
                if (end < 0) break;
                Word(text[i..end].Trim());
                i = end + 1;
            }
        }
        FlushBatch();
        _img.Filled = Geo.Union(_img.Filled); // normalizar orientaciones
    }

    // ---- comandos extendidos (%...%) ----

    private void Extended(string cmd, bool firstOfBlock)
    {
        if (cmd.Length == 0) return;

        // cuerpo de una macro AM: lÃ­neas que siguen al %AMNOMBRE dentro del mismo bloque
        if (!firstOfBlock && _pendingMacro != null)
        {
            _macros[_pendingMacro].Add(cmd);
            return;
        }

        if (cmd.StartsWith("AM"))
        {
            _pendingMacro = cmd[2..].Trim();
            _macros[_pendingMacro] = new List<string>();
            return;
        }
        _pendingMacro = null;

        if (cmd.Length < 2) return;
        switch (cmd[..2])
        {
            case "FS":
            {
                int xi = cmd.IndexOf('X');
                _trailingOmitted = cmd.Contains('T') && (xi < 0 || cmd.IndexOf('T') < xi);
                if (xi >= 0 && xi + 2 < cmd.Length && char.IsDigit(cmd[xi + 1]) && char.IsDigit(cmd[xi + 2]))
                {
                    _intDigits = cmd[xi + 1] - '0';
                    _decimals = cmd[xi + 2] - '0';
                }
                break;
            }
            case "MO":
                _unit = cmd.Contains("IN") ? 25.4 : 1.0;
                break;
            case "AD":
                DefineAperture(cmd[2..]);
                break;
            case "LP":
                FlushBatch();
                _dark = !cmd.Contains('C');
                break;
            case "LN" or "IP" or "OF" or "SF" or "IN":
                break; // informativos o identidad
            default:
                if (cmd[..2] is "SR" or "MI" or "AS" or "IR")
                    Warn($"Comando Gerber no soportado: %{cmd[..2]}% (se ignora).");
                break;
        }
    }

    private void DefineAperture(string spec)
    {
        // D10C,1.5 / D11R,2X1 / D12O,2X1 / D13P,2X6X30 / D14MACRO,1X0.5
        if (spec.Length < 2 || spec[0] != 'D') return;
        int i = 1;
        while (i < spec.Length && char.IsDigit(spec[i])) i++;
        if (!int.TryParse(spec[1..i], out int num)) return;
        string rest = spec[i..];
        int comma = rest.IndexOf(',');
        string name = comma < 0 ? rest : rest[..comma];
        double[] args = comma < 0
            ? Array.Empty<double>()
            : rest[(comma + 1)..]
                .Split('X', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

        if (name is "C" or "R" or "O" or "P")
            _apertures[num] = new Aperture(name[0], args, null);
        else if (_macros.ContainsKey(name))
            _apertures[num] = new Aperture('M', args, name);
        else
        {
            Warn($"Apertura D{num} usa la macro desconocida '{name}'; se trata como cÃ­rculo pequeÃ±o.");
            _apertures[num] = new Aperture('C', args.Length > 0 ? args : new[] { 0.1 }, null);
        }
    }

    // ---- comandos de palabra (terminados en *) ----

    private void Word(string w)
    {
        if (w.Length == 0 || w.StartsWith("G04")) return;

        while (w.StartsWith('G'))
        {
            int j = 1;
            while (j < w.Length && char.IsDigit(w[j])) j++;
            if (j == 1) break;
            switch (int.Parse(w[1..j]))
            {
                case 1: _interp = 1; break;
                case 2: _interp = 2; break;
                case 3: _interp = 3; break;
                case 36: CloseRegionContour(); _inRegion = true; break;
                case 37: CloseRegionContour(); _inRegion = false; break;
                case 74: _multiQuadrant = false; Warn("Arcos G74 (un cuadrante): soporte aproximado."); break;
                case 75: _multiQuadrant = true; break;
                case 70: _unit = 25.4; break;
                case 71: _unit = 1.0; break;
                case 54 or 55 or 90: break;
                case 91: Warn("Coordenadas incrementales G91 no soportadas."); break;
            }
            w = w[j..];
        }
        if (w.Length == 0) return;
        if (w[0] == 'M') return; // M00/M01/M02: fin

        // selecciÃ³n de apertura pura: D10, D11â€¦
        if (w[0] == 'D' && !w.Contains('X') && !w.Contains('Y') && !w.Contains('I') && !w.Contains('J'))
        {
            int code = int.Parse(w[1..]);
            if (code >= 10)
            {
                _current = _apertures.TryGetValue(code, out var ap) ? ap : null;
                if (_current == null) Warn($"Apertura D{code} no definida.");
            }
            else
            {
                Operate(code, _x, _y, 0, 0, hasIJ: false);
            }
            return;
        }

        long x = _x, y = _y, iOfs = 0, jOfs = 0;
        bool hasIJ = false;
        int op = -1;
        int k = 0;
        while (k < w.Length)
        {
            char key = w[k++];
            int start = k;
            while (k < w.Length && (char.IsDigit(w[k]) || w[k] is '+' or '-' or '.')) k++;
            string num = w[start..k];
            if (num.Length == 0) continue;
            if (key == 'D') { op = int.Parse(num); continue; }
            long val = ParseCoord(num);
            switch (key)
            {
                case 'X': x = val; break;
                case 'Y': y = val; break;
                case 'I': iOfs = val; hasIJ = true; break;
                case 'J': jOfs = val; hasIJ = true; break;
                default: Warn($"Dato Gerber desconocido '{key}' (se ignora)."); break;
            }
        }
        if (op < 0) op = 1; // D01 modal implÃ­cito (archivos viejos)
        Operate(op, x, y, iOfs, jOfs, hasIJ);
    }

    private long ParseCoord(string s)
    {
        if (s.Contains('.'))
            return Geo.L(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) * _unit);
        bool neg = s[0] == '-';
        string digits = s.TrimStart('+', '-');
        if (_trailingOmitted)
            digits = digits.PadRight(_intDigits + _decimals, '0');
        if (digits.Length == 0) return 0;
        double mm = long.Parse(digits) / Math.Pow(10, _decimals) * _unit;
        return (neg ? -1 : 1) * Geo.L(mm);
    }

    private void Operate(int op, long x, long y, long i, long j, bool hasIJ)
    {
        switch (op)
        {
            case 2: // mover
                CloseRegionContour();
                break;
            case 3: // flash
                if (_inRegion) Warn("Flash dentro de regiÃ³n: se ignora.");
                else Flash(x, y);
                break;
            case 1: // dibujar
                if (!_hasPos) break; // sin posiciÃ³n previa: actÃºa como move
                if (_interp == 1 || !hasIJ)
                {
                    if (_interp != 1 && !hasIJ) Warn("Arco sin I/J: se traza recto.");
                    DrawSegment(new Point64(_x, _y), new Point64(x, y));
                }
                else
                {
                    DrawArc(new Point64(_x, _y), new Point64(x, y), i, j, _interp == 2);
                }
                break;
        }
        _x = x; _y = y; _hasPos = true;
    }

    private void DrawSegment(Point64 a, Point64 b)
    {
        if (_inRegion)
        {
            if (_contour.Count == 0) _contour.Add(a);
            _contour.Add(b);
            return;
        }
        if (a == b) return;
        _img.Segments.Add((Geo.MmPt(a), Geo.MmPt(b)));
        if (_current == null) { Warn("Trazo sin apertura seleccionada."); return; }
        double width = (_current.Kind == 'C' ? _current.W : Math.Min(_current.W, _current.H)) * _unit;
        if (_current.Kind != 'C')
            Warn($"Trazo con apertura tipo '{_current.Kind}': se usa ancho {width:0.###} mm con extremos redondos.");
        if (width <= 0) return;
        AddBatch(Geo.StrokeSegment(a, b, width));
    }

    private void DrawArc(Point64 from, Point64 to, long i, long j, bool clockwise)
    {
        double cx, cy;
        if (_multiQuadrant)
        {
            cx = Geo.Mm(from.X + i);
            cy = Geo.Mm(from.Y + j);
        }
        else
        {
            (cx, cy) = PickSingleQuadrantCenter(from, to, Math.Abs(i), Math.Abs(j));
        }
        var pts = TessellateArc(Geo.Mm(from.X), Geo.Mm(from.Y), Geo.Mm(to.X), Geo.Mm(to.Y), cx, cy, clockwise);
        var prev = from;
        foreach (var (px, py) in pts)
        {
            var cur = Geo.Pt(px, py);
            DrawSegment(prev, cur);
            prev = cur;
        }
    }

    private static (double, double) PickSingleQuadrantCenter(Point64 from, Point64 to, long ai, long aj)
    {
        double best = double.MaxValue, bx = Geo.Mm(from.X + ai), by = Geo.Mm(from.Y + aj);
        foreach (long si in new[] { 1L, -1L })
            foreach (long sj in new[] { 1L, -1L })
            {
                double cx = Geo.Mm(from.X + si * ai), cy = Geo.Mm(from.Y + sj * aj);
                double r1 = double.Hypot(Geo.Mm(from.X) - cx, Geo.Mm(from.Y) - cy);
                double r2 = double.Hypot(Geo.Mm(to.X) - cx, Geo.Mm(to.Y) - cy);
                double err = Math.Abs(r1 - r2);
                if (err < best) { best = err; bx = cx; by = cy; }
            }
        return (bx, by);
    }

    /// <summary>Puntos del arco (sin incluir el inicio). Inicio == fin â‡’ cÃ­rculo completo.</summary>
    private static List<(double x, double y)> TessellateArc(double x0, double y0, double x1, double y1,
        double cx, double cy, bool clockwise)
    {
        var pts = new List<(double, double)>();
        double r = double.Hypot(x0 - cx, y0 - cy);
        if (r < 1e-9) { pts.Add((x1, y1)); return pts; }
        double a0 = Math.Atan2(y0 - cy, x0 - cx);
        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        double sweep = clockwise ? a0 - a1 : a1 - a0;
        while (sweep <= 1e-12) sweep += 2 * Math.PI;
        double step = Math.Acos(Math.Max(0.5, 1 - 0.002 / r));
        int n = Math.Clamp((int)Math.Ceiling(sweep / step), 2, 720);
        for (int k = 1; k <= n; k++)
        {
            double a = clockwise ? a0 - sweep * k / n : a0 + sweep * k / n;
            pts.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return pts;
    }

    private void Flash(long x, long y)
    {
        if (_current == null) { Warn("Flash sin apertura seleccionada."); return; }
        double cx = Geo.Mm(x), cy = Geo.Mm(y);
        var ap = _current;
        // las dimensiones de las aperturas estándar vienen en las unidades del archivo
        Paths64 shape = ap.Kind switch
        {
            'C' => new Paths64 { Geo.Circle(cx, cy, ap.W * _unit / 2) },
            'R' => new Paths64 { Geo.Rect(cx, cy, ap.W * _unit, ap.H * _unit) },
            'O' => new Paths64 { Geo.Obround(cx, cy, ap.W * _unit, ap.H * _unit) },
            'P' => new Paths64 { RegularPolygon(cx, cy, ap.Args, _unit) },
            'M' => MacroFlash(ap.MacroName!, ap.Args, cx, cy),
            _ => new Paths64(),
        };
        double hole = _unit * (ap.Kind switch
        {
            'C' => ap.Args.Length > 1 ? ap.Args[1] : 0,
            'R' or 'O' => ap.Args.Length > 2 ? ap.Args[2] : 0,
            'P' => ap.Args.Length > 3 ? ap.Args[3] : 0,
            _ => 0,
        });
        if (hole > 0)
            shape = Geo.Difference(shape, new Paths64 { Geo.Circle(cx, cy, hole / 2) });
        AddBatch(shape);
    }

    private static Path64 RegularPolygon(double cx, double cy, double[] args, double unit)
    {
        double d = (args.Length > 0 ? args[0] : 0) * unit;
        int n = args.Length > 1 ? Math.Max(3, (int)args[1]) : 6;
        double rot = args.Length > 2 ? args[2] : 0;
        var path = new Path64(n);
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / n + rot * Math.PI / 180.0;
            path.Add(Geo.Pt(cx + d / 2 * Math.Cos(a), cy + d / 2 * Math.Sin(a)));
        }
        return path;
    }

    // ---- macros de apertura (AM) ----

    private Paths64 MacroFlash(string macroName, double[] args, double cx, double cy)
    {
        var result = new Paths64();
        var vars = new Dictionary<int, double>();
        for (int k = 0; k < args.Length; k++) vars[k + 1] = args[k];

        foreach (string raw in _macros[macroName])
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("0 ") || line == "0") continue; // comentario
            if (line.StartsWith('$'))
            {
                int eq = line.IndexOf('=');
                if (eq > 1 && int.TryParse(line[1..eq], out int vn))
                    vars[vn] = MacroExpr.Eval(line[(eq + 1)..], vars);
                continue;
            }
            double[] f;
            try
            {
                f = line.Split(',').Select(s => MacroExpr.Eval(s.Trim(), vars)).ToArray();
            }
            catch (Exception)
            {
                Warn($"ExpresiÃ³n de macro '{macroName}' no entendida: '{line}'.");
                continue;
            }
            int code = (int)f[0];
            bool exposure = f.Length < 2 || f[1] != 0;
            Paths64 prim = MacroPrimitive(code, f, macroName);
            if (prim.Count == 0) continue;
            result = exposure ? Geo.Union(result, prim) : Geo.Difference(result, prim);
        }

        var moved = new Paths64(result.Count);
        long dx = Geo.L(cx), dy = Geo.L(cy);
        foreach (var path in result)
        {
            var np = new Path64(path.Count);
            foreach (var p in path) np.Add(new Point64(p.X + dx, p.Y + dy));
            moved.Add(np);
        }
        return moved;
    }

    /// <summary>Primitiva de macro ya evaluada, centrada en el origen del flash. Dimensiones en unidades del archivo.</summary>
    private Paths64 MacroPrimitive(int code, double[] f, string macroName)
    {
        double U(double v) => v * _unit;
        switch (code)
        {
            case 1: // cÃ­rculo: 1, exp, diÃ¡m, cx, cy [, rot]
            {
                double rot = f.Length > 5 ? f[5] : 0;
                var (px, py) = Geo.Rotate(U(f[3]), U(f[4]), rot);
                return new Paths64 { Geo.Circle(px, py, U(f[2]) / 2) };
            }
            case 20: // lÃ­nea vector: 20, exp, ancho, x1, y1, x2, y2, rot
            {
                double rot = f.Length > 7 ? f[7] : 0;
                var (x1, y1) = Geo.Rotate(U(f[3]), U(f[4]), rot);
                var (x2, y2) = Geo.Rotate(U(f[5]), U(f[6]), rot);
                // extremos cuadrados: rectÃ¡ngulo explÃ­cito
                double dxl = x2 - x1, dyl = y2 - y1;
                double len = double.Hypot(dxl, dyl);
                if (len < 1e-9) return new Paths64();
                double hw = U(f[2]) / 2;
                double nx = -dyl / len * hw, ny = dxl / len * hw;
                return new Paths64 { new Path64
                {
                    Geo.Pt(x1 + nx, y1 + ny), Geo.Pt(x2 + nx, y2 + ny),
                    Geo.Pt(x2 - nx, y2 - ny), Geo.Pt(x1 - nx, y1 - ny),
                } };
            }
            case 21: // lÃ­nea centrada: 21, exp, ancho, alto, cx, cy, rot
            {
                double rot = f.Length > 6 ? f[6] : 0;
                var rect = new Path64(4);
                foreach (var (ox, oy) in new[]
                         { (-U(f[2]) / 2, -U(f[3]) / 2), (U(f[2]) / 2, -U(f[3]) / 2),
                           (U(f[2]) / 2, U(f[3]) / 2), (-U(f[2]) / 2, U(f[3]) / 2) })
                {
                    var (rx, ry) = Geo.Rotate(U(f[4]) + ox, U(f[5]) + oy, rot);
                    rect.Add(Geo.Pt(rx, ry));
                }
                return new Paths64 { rect };
            }
            case 4: // contorno: 4, exp, n, x0, y0, x1, y1, â€¦, rot
            {
                int n = (int)f[2];
                int last = 3 + 2 * (n + 1);
                double rot = f.Length > last ? f[last] : 0;
                var path = new Path64(n + 1);
                for (int k = 0; k <= n && 4 + 2 * k < f.Length; k++)
                {
                    var (rx, ry) = Geo.Rotate(U(f[3 + 2 * k]), U(f[4 + 2 * k]), rot);
                    path.Add(Geo.Pt(rx, ry));
                }
                return new Paths64 { path };
            }
            case 5: // polÃ­gono regular: 5, exp, n, cx, cy, diÃ¡m, rot
            {
                int n = Math.Max(3, (int)f[2]);
                double rot = f.Length > 6 ? f[6] : 0;
                var path = new Path64(n);
                for (int k = 0; k < n; k++)
                {
                    double a = 2 * Math.PI * k / n;
                    var (rx, ry) = Geo.Rotate(U(f[3]) + U(f[5]) / 2 * Math.Cos(a),
                                              U(f[4]) + U(f[5]) / 2 * Math.Sin(a), rot);
                    path.Add(Geo.Pt(rx, ry));
                }
                return new Paths64 { path };
            }
            case 7: // tÃ©rmico: 7, cx, cy, diÃ¡m ext, diÃ¡m int, grosor cruz, rot
            {
                double cx = U(f[1]), cy = U(f[2]);
                double rot = f.Length > 6 ? f[6] : 0;
                var ring = Geo.Difference(
                    new Paths64 { Geo.Circle(cx, cy, U(f[3]) / 2) },
                    new Paths64 { Geo.Circle(cx, cy, U(f[4]) / 2) });
                double gap = U(f[5]);
                double span = U(f[3]) + 1;
                var cross = new Paths64
                {
                    Geo.Rect(cx, cy, span, gap, rot),
                    Geo.Rect(cx, cy, gap, span, rot + 90),
                };
                return Geo.Difference(ring, cross);
            }
            default:
                Warn($"Primitiva {code} de la macro '{macroName}' no soportada (se ignora).");
                return new Paths64();
        }
    }

    // ---- acumulaciÃ³n con polaridad ----

    private void AddBatch(Paths64 shape) => _batch.AddRange(shape);

    private void CloseRegionContour()
    {
        if (_contour.Count >= 3)
        {
            var contour = _contour;
            if (!Clipper.IsPositive(contour))
                contour.Reverse();
            AddBatch(new Paths64 { contour });
        }
        _contour = new Path64();
    }

    private void FlushBatch()
    {
        CloseRegionContour();
        if (_batch.Count == 0) return;
        _img.Filled = _dark
            ? Geo.Union(_img.Filled, Geo.Union(_batch))
            : Geo.Difference(_img.Filled, Geo.Union(_batch));
        _batch = new Paths64();
    }
}

