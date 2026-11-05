using System.Globalization;
using System.Text;
using System.Text.Json;
using Clipper2Lib;
using Tostatronic.Cnc.Core.GCode;
using Tostatronic.Cnc.Core.Motion;

namespace Tostatronic.Cnc.Cam.Jobs;

/// <summary>Una trayectoria de corte de un .nc: lo que la herramienta recorre entre una bajada y la siguiente subida.</summary>
public sealed class RepasoPath
{
    public List<PointD> Points { get; } = new();

    /// <summary>Profundidad de corte (Z negativa) con la que se bajó.</summary>
    public double Z { get; set; }

    /// <summary>Índice (base 0) de la línea del archivo donde empieza (la subida a Z segura que la precede).</summary>
    public int SourceIndex { get; set; }

    public double LengthMm
    {
        get
        {
            double t = 0;
            for (int i = 1; i < Points.Count; i++)
                t += double.Hypot(Points[i].x - Points[i - 1].x, Points[i].y - Points[i - 1].y);
            return t;
        }
    }

    public bool IsClosed => Points.Count >= 3 &&
                            double.Hypot(Points[0].x - Points[^1].x, Points[0].y - Points[^1].y) < 1e-6;

    public RectMm Bounds
    {
        get
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in Points)
            {
                x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y);
                x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y);
            }
            return new RectMm(x0, y0, x1, y1);
        }
    }

    // ---- posiciones a lo largo de la trayectoria ----
    // Una posición se expresa con un parámetro u = índice del segmento + fracción recorrida de ese
    // segmento (0 ≤ u ≤ Points.Count − 1). Así una sección (RepasoSection) no depende de redondeos
    // de coordenadas y se puede guardar en el .repaso.json.

    /// <summary>Último valor válido del parámetro (el último punto).</summary>
    public double MaxU => Points.Count - 1;

    /// <summary>Punto de la trayectoria en el parámetro u (se recorta al rango válido).</summary>
    public PointD At(double u)
    {
        if (Points.Count == 0) return new PointD(0, 0);
        u = Math.Clamp(u, 0, MaxU);
        int k = Math.Min((int)Math.Floor(u), Points.Count - 2);
        if (k < 0) return Points[0];
        double t = u - k;
        var a = Points[k]; var b = Points[k + 1];
        return new PointD(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }

    /// <summary>Parámetro del punto de la trayectoria más cercano a (x, y), y la distancia hasta él.</summary>
    public (double U, double DistMm) Project(double x, double y)
    {
        double bestU = 0, bestD = double.MaxValue;
        for (int k = 1; k < Points.Count; k++)
        {
            var a = Points[k - 1]; var b = Points[k];
            double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((x - a.x) * dx + (y - a.y) * dy) / l2, 0, 1) : 0;
            double d = double.Hypot(x - (a.x + t * dx), y - (a.y + t * dy));
            if (d < bestD) { bestD = d; bestU = k - 1 + t; }
        }
        return (bestU, bestD);
    }

    /// <summary>
    /// ¿El parámetro u cae en el recorrido que va hacia adelante de <paramref name="from"/> a
    /// <paramref name="to"/>? Si to &lt; from el recorrido da la vuelta por el cierre (solo tiene
    /// sentido en trayectorias cerradas).
    /// </summary>
    public static bool ContainsForward(double from, double to, double u) =>
        to >= from ? u >= from && u <= to : u >= from || u <= to;

    /// <summary>Puntos del tramo que va hacia adelante de from a to (dando la vuelta por el cierre si to &lt; from).</summary>
    public List<PointD> Slice(double from, double to)
    {
        int n = Points.Count;
        var outPts = new List<PointD>();
        if (n < 2) return outPts;
        from = Math.Clamp(from, 0, MaxU); to = Math.Clamp(to, 0, MaxU);
        if (to < from && !IsClosed) (from, to) = (to, from);
        Add(outPts, At(from));
        if (to >= from)
        {
            for (int k = (int)Math.Floor(from) + 1; k <= (int)Math.Ceiling(to) - 1; k++) Add(outPts, Points[k]);
        }
        else
        {
            for (int k = (int)Math.Floor(from) + 1; k <= n - 1; k++) Add(outPts, Points[k]); // Points[n-1] es el cierre (= Points[0])
            for (int k = 1; k <= (int)Math.Ceiling(to) - 1; k++) Add(outPts, Points[k]);
        }
        Add(outPts, At(to));
        return outPts;

        static void Add(List<PointD> l, PointD p)
        {
            if (l.Count == 0 || double.Hypot(l[^1].x - p.x, l[^1].y - p.y) > 1e-6) l.Add(p);
        }
    }

    /// <summary>Largo en mm del tramo de from a to (hacia adelante).</summary>
    public double SliceLengthMm(double from, double to)
    {
        var pts = Slice(from, to);
        double t = 0;
        for (int i = 1; i < pts.Count; i++) t += double.Hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y);
        return t;
    }

    /// <summary>
    /// De los dos recorridos posibles entre ua y ub (en una trayectoria cerrada hay dos), el que
    /// pasa por más de los parámetros de <paramref name="samples"/>; sin muestras (o empate), el
    /// más corto. En una trayectoria abierta siempre va del menor al mayor.
    /// </summary>
    public (double From, double To) ChooseArc(double ua, double ub, IEnumerable<double>? samples = null)
    {
        if (!IsClosed) return ua <= ub ? (ua, ub) : (ub, ua);
        int fwd = 0, back = 0;
        if (samples != null)
            foreach (double u in samples)
            {
                if (ContainsForward(ua, ub, u)) fwd++;
                if (ContainsForward(ub, ua, u)) back++;
            }
        if (fwd != back) return fwd > back ? (ua, ub) : (ub, ua);
        return SliceLengthMm(ua, ub) <= SliceLengthMm(ub, ua) ? (ua, ub) : (ub, ua);
    }
}

/// <summary>
/// Sección de una trayectoria: solo el tramo entre dos posiciones (parámetros de RepasoPath), hacia
/// adelante; si To &lt; From en una trayectoria cerrada, el tramo da la vuelta por el cierre. Sirve
/// para repasar una parte de una pista enorme (p. ej. donde la placa se pandea) sin repetirla toda.
/// </summary>
public sealed class RepasoSection
{
    public int Path { get; set; }
    public double From { get; set; }
    public double To { get; set; }

    public RepasoSection() { }
    public RepasoSection(int path, double from, double to) { Path = path; From = from; To = to; }
}

/// <summary>Rectángulo en mm de trabajo (X0 ≤ X1, Y0 ≤ Y1).</summary>
public readonly record struct RectMm(double X0, double Y0, double X1, double Y1)
{
    public static RectMm FromCorners(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));

    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
}

/// <summary>Resultado de exportar una selección de repaso.</summary>
public sealed class RepasoExport
{
    public required string Content { get; init; }
    public int Pieces { get; init; }
    public double LengthMm { get; init; }
    public int FullPaths { get; init; }
    public int Zones { get; init; }
    public int Sections { get; init; }
    public double DepthMm { get; init; }
}

/// <summary>
/// Repaso de pistas: lee un .nc de aislamiento (el que genera este programa, los de pcb_cnc.py /
/// detector_cnc.py, o cualquier G-code plano con bajadas en Z), lo separa en trayectorias (una por
/// bajada) y vuelve a emitir solo las elegidas: trayectorias completas marcadas a mano, secciones
/// (un tramo entre dos puntos de una trayectoria) y/o los tramos de las demás que caen dentro de
/// zonas rectangulares, con la profundidad que se pida.
/// El origen no se mueve: el repaso se corre con los mismos ceros que el aislamiento.
/// Es el mismo flujo que selector_repaso.html del proyecto del detector, ya dentro de la app.
/// </summary>
public sealed class RepasoProgram
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Name { get; private set; } = "";
    public List<string> Header { get; } = new();
    public List<string> Footer { get; } = new();
    public List<RepasoPath> Paths { get; } = new();

    /// <summary>Z de subida entre trayectorias y Z de traslado final, como venían en el archivo.</summary>
    public double SafeZMm { get; private set; } = 2.0;
    public double TravelZMm { get; private set; } = 15.0;
    public int FeedMmMin { get; private set; } = 150;
    public int PlungeMmMin { get; private set; } = 50;

    /// <summary>Profundidad (positiva) a la que cortaba el archivo: la más común entre las trayectorias.</summary>
    public double DepthMm { get; private set; } = 0.1;

    public double TotalLengthMm => Paths.Sum(p => p.LengthMm);

    public RectMm Bounds
    {
        get
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in Paths)
            {
                var b = p.Bounds;
                x0 = Math.Min(x0, b.X0); y0 = Math.Min(y0, b.Y0); x1 = Math.Max(x1, b.X1); y1 = Math.Max(y1, b.Y1);
            }
            return Paths.Count == 0 ? new RectMm(0, 0, 0, 0) : new RectMm(x0, y0, x1, y1);
        }
    }

    public static RepasoProgram Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static RepasoProgram Parse(string text, string name)
    {
        var prog = new RepasoProgram { Name = name };
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var parser = new GCodeParser();

        double x = 0, y = 0, z = 0, maxZ = 0;
        bool up = true;
        RepasoPath? cur = null;
        int? pendingRetract = null;      // línea de la última subida (inicia la siguiente trayectoria)
        int lastCutIndex = -1;
        bool feedSeen = false, plungeSeen = false;
        var retractZs = new List<double>();          // Z de subida después de cada trayectoria
        var upLines = new List<(int Line, double Z)>(); // todos los rápidos en alto, para hallar el cierre

        for (int i = 0; i < raw.Length; i++)
        {
            GCodeLine? gl;
            try { gl = parser.ParseLine(raw[i], i); }
            catch (GCodeException) { continue; }
            if (gl == null || !gl.IsMotion) continue;

            double nx = gl.X ?? x, ny = gl.Y ?? y, nz = gl.Z ?? z;
            if (gl.IsRapid)
            {
                if (gl.Z != null && nz >= 0)
                {
                    // en alto (subida o descenso a la Z segura): cierra la trayectoria en curso
                    if (cur != null && cur.Points.Count >= 2)
                    {
                        prog.Paths.Add(cur);
                        retractZs.Add(nz); // la Z a la que se sube entre trayectorias
                    }
                    cur = null;
                    up = true;
                    pendingRetract = i;
                    upLines.Add((i, nz));
                }
                maxZ = Math.Max(maxZ, nz);
            }
            else if (gl.Z != null && nz < 0 && gl.X == null && gl.Y == null && !gl.IsArc)
            {
                // bajada vertical: empieza una trayectoria en la posición XY actual
                if (cur == null || up)
                {
                    cur = new RepasoPath { Z = nz, SourceIndex = pendingRetract ?? i };
                    cur.Points.Add(new PointD(x, y));
                }
                else
                {
                    cur.Z = Math.Min(cur.Z, nz); // segunda bajada en el mismo sitio
                }
                up = false;
                if (gl.F is double pf && !plungeSeen) { prog.PlungeMmMin = (int)Math.Round(pf); plungeSeen = true; }
                lastCutIndex = i;
            }
            else if (!up && cur != null && (gl.X != null || gl.Y != null))
            {
                if (gl.IsArc)
                {
                    foreach (var (px, py) in ArcTessellator.Tessellate(x, y, nx, ny, gl.I!.Value, gl.J!.Value, gl.Command == "G02"))
                        cur.Points.Add(new PointD(px, py));
                }
                else
                {
                    cur.Points.Add(new PointD(nx, ny));
                }
                if (gl.F is double f && !feedSeen) { prog.FeedMmMin = (int)Math.Round(f); feedSeen = true; }
                if (nz < 0) lastCutIndex = i;
            }
            x = nx; y = ny; z = nz;
        }
        if (cur != null && cur.Points.Count >= 2) prog.Paths.Add(cur);

        if (prog.Paths.Count == 0)
            throw new InvalidDataException("No encontré trayectorias de corte: el archivo no tiene bajadas (G1 Z negativa) seguidas de movimientos XY.");

        // alturas: traslado = la Z más alta; segura = la subida más común entre trayectorias (o la Z
        // del rápido que inicia la primera, si es la única)
        prog.TravelZMm = maxZ > 0 ? maxZ : 15.0;
        var betweenPaths = retractZs.Where(v => v > 0 && v < prog.TravelZMm - 1e-9).ToList();
        if (betweenPaths.Count > 0)
            prog.SafeZMm = betweenPaths.GroupBy(v => Math.Round(v, 3)).OrderByDescending(g => g.Count()).First().Key;
        else
        {
            var first = upLines.Where(u => u.Line == prog.Paths[0].SourceIndex && u.Z > 0 && u.Z < prog.TravelZMm - 1e-9).ToList();
            prog.SafeZMm = first.Count > 0 ? first[0].Z : Math.Min(2.0, prog.TravelZMm);
        }

        prog.DepthMm = Math.Abs(prog.Paths.GroupBy(p => Math.Round(p.Z, 3)).OrderByDescending(g => g.Count()).First().Key);

        // encabezado: todo lo anterior a la subida que inicia la primera trayectoria
        int headerEnd = prog.Paths[0].SourceIndex;
        for (int i = 0; i < headerEnd; i++) prog.Header.Add(raw[i]);
        if (prog.Header.Count == 0)
            prog.Header.AddRange(new[] { $"( {name} )", "G21 G90 G17 G94", $"G0 Z{prog.TravelZMm.ToString("0.000", Inv)}" });

        // cierre: desde la primera subida a la Z de traslado después del último corte
        int footerStart = -1;
        foreach (var (line, zUp) in upLines)
            if (line > lastCutIndex && Math.Abs(zUp - prog.TravelZMm) < 1e-9) { footerStart = line; break; }
        if (footerStart >= 0)
        {
            for (int i = footerStart; i < raw.Length; i++)
                if (raw[i].Trim().Length > 0) prog.Footer.Add(raw[i]);
        }
        else
        {
            prog.Footer.AddRange(new[] { $"G0 Z{prog.TravelZMm.ToString("0.000", Inv)}", "M5", "G0 X0 Y0", "M2" });
        }
        return prog;
    }

    /// <summary>Índice de la trayectoria más cercana a un punto (distancia al segmento más próximo), o −1 si ninguna está a menos de maxDistMm.</summary>
    public int Pick(double x, double y, double maxDistMm)
    {
        int best = -1;
        double bestD = maxDistMm;
        for (int i = 0; i < Paths.Count; i++)
        {
            var pts = Paths[i].Points;
            for (int k = 1; k < pts.Count; k++)
            {
                double d = SegmentDistance(x, y, pts[k - 1], pts[k]);
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        return best;
    }

    private static double SegmentDistance(double px, double py, PointD a, PointD b)
    {
        double dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / l2, 0, 1) : 0;
        return double.Hypot(px - (a.x + t * dx), py - (a.y + t * dy));
    }

    /// <summary>
    /// G-code con las trayectorias marcadas completas más, de las no marcadas, los tramos que caen
    /// dentro de las zonas. Mismo encabezado y cierre que el archivo original (mismo origen), a la
    /// profundidad pedida, en orden de vecino más cercano desde X0 Y0.
    /// </summary>
    public RepasoExport Export(IEnumerable<int> selected, IReadOnlyList<RectMm> zones, double depthMm) =>
        Export(selected, zones, Array.Empty<RepasoSection>(), depthMm);

    /// <summary>
    /// Igual que <see cref="Export(IEnumerable{int}, IReadOnlyList{RectMm}, double)"/> más las
    /// secciones: de una trayectoria con secciones solo salen esas secciones (ni completa, ni
    /// recortada por zonas). Si la trayectoria además está marcada completa, gana la completa.
    /// </summary>
    public RepasoExport Export(IEnumerable<int> selected, IReadOnlyList<RectMm> zones, IReadOnlyList<RepasoSection> sections, double depthMm)
    {
        var sel = new HashSet<int>(selected.Where(i => i >= 0 && i < Paths.Count));
        var secs = sections.Where(s => s.Path >= 0 && s.Path < Paths.Count && !sel.Contains(s.Path)).ToList();
        var withSections = new HashSet<int>(secs.Select(s => s.Path));
        depthMm = Math.Abs(depthMm);
        var pieces = new List<List<PointD>>();
        foreach (int i in sel) pieces.Add(Paths[i].Points);
        foreach (var s in secs)
        {
            var pts = Paths[s.Path].Slice(s.From, s.To);
            if (pts.Count >= 2) pieces.Add(pts);
        }
        for (int i = 0; i < Paths.Count; i++)
        {
            if (sel.Contains(i) || withSections.Contains(i)) continue;
            foreach (var zone in zones)
                pieces.AddRange(ClipPath(Paths[i].Points, zone));
        }
        if (pieces.Count == 0)
            throw new InvalidOperationException("No hay nada marcado, ni secciones, ni zonas dibujadas.");

        var ordered = Order(pieces);
        var g = new StringBuilder();
        string title = Header.Count > 0 ? Header[0] : $"( {Name} )";
        g.AppendLine(title.TrimEnd());
        string secTxt = secs.Count > 0 ? $" + {secs.Count} secciones" : "";
        g.AppendLine($"( Repaso: {sel.Count} trayectorias completas{secTxt} + {zones.Count} zonas = {ordered.Count} tramos, a {depthMm.ToString("0.0##", Inv)} mm )");
        g.AppendLine($"( Origen: {Name}. Mismos ceros X0 Y0 Z0 que el aislamiento )");
        for (int i = 1; i < Header.Count; i++)
            if (Header[i].Trim().Length > 0) g.AppendLine(Header[i].TrimEnd());

        double mm = 0;
        foreach (var pts in ordered)
        {
            g.AppendLine($"G0 Z{N(SafeZMm)}");
            g.AppendLine($"G0 X{N(pts[0].x)} Y{N(pts[0].y)}");
            g.AppendLine($"G1 Z{N(-depthMm)} F{PlungeMmMin}");
            for (int k = 1; k < pts.Count; k++)
            {
                g.Append($"G1 X{N(pts[k].x)} Y{N(pts[k].y)}");
                g.AppendLine(k == 1 ? $" F{FeedMmMin}" : "");
                mm += double.Hypot(pts[k].x - pts[k - 1].x, pts[k].y - pts[k - 1].y);
            }
        }
        foreach (var l in Footer) g.AppendLine(l.TrimEnd());
        return new RepasoExport
        {
            Content = g.ToString(),
            Pieces = ordered.Count,
            LengthMm = mm,
            FullPaths = sel.Count,
            Zones = zones.Count,
            Sections = secs.Count,
            DepthMm = depthMm,
        };
    }

    public static string SuggestedFileName(string sourceName)
    {
        string stem = Path.GetFileNameWithoutExtension(sourceName);
        return stem + "_seleccion.nc";
    }

    private static string N(double v) => v.ToString("0.000", Inv);

    /// <summary>Vecino más cercano desde X0 Y0 por el punto inicial de cada tramo (como order() de los scripts).</summary>
    private static List<List<PointD>> Order(List<List<PointD>> pieces)
    {
        var rest = pieces.ToList();
        var outList = new List<List<PointD>>(rest.Count);
        var cur = new PointD(0, 0);
        while (rest.Count > 0)
        {
            int bi = 0;
            double bd = double.MaxValue;
            for (int i = 0; i < rest.Count; i++)
            {
                double d = double.Hypot(rest[i][0].x - cur.x, rest[i][0].y - cur.y);
                if (d < bd) { bd = d; bi = i; }
            }
            var r = rest[bi];
            rest.RemoveAt(bi);
            outList.Add(r);
            cur = r[^1];
        }
        return outList;
    }

    /// <summary>Tramos de una trayectoria que quedan dentro del rectángulo (Liang–Barsky por segmento, unidos cuando son consecutivos).</summary>
    public static List<List<PointD>> ClipPath(IReadOnlyList<PointD> pts, RectMm z)
    {
        var runs = new List<List<PointD>>();
        List<PointD>? run = null;
        for (int k = 1; k < pts.Count; k++)
        {
            var s = ClipSegment(pts[k - 1], pts[k], z);
            if (s == null) { run = null; continue; }
            var (a, b) = s.Value;
            if (run != null && double.Hypot(run[^1].x - a.x, run[^1].y - a.y) < 1e-6)
                run.Add(b);
            else
            {
                run = new List<PointD> { a, b };
                runs.Add(run);
            }
        }
        return runs.Where(r => r.Count >= 2 && r.Skip(1).Select((q, i) => double.Hypot(q.x - r[i].x, q.y - r[i].y)).Any(d => d > 1e-4)).ToList();
    }

    private static (PointD A, PointD B)? ClipSegment(PointD a, PointD b, RectMm z)
    {
        double t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
        foreach (var (p, q) in new[] { (-dx, a.x - z.X0), (dx, z.X1 - a.x), (-dy, a.y - z.Y0), (dy, z.Y1 - a.y) })
        {
            if (p == 0) { if (q < 0) return null; continue; }
            double r = q / p;
            if (p < 0) { if (r > t1) return null; if (r > t0) t0 = r; }
            else { if (r < t0) return null; if (r < t1) t1 = r; }
        }
        return (new PointD(a.x + t0 * dx, a.y + t0 * dy), new PointD(a.x + t1 * dx, a.y + t1 * dy));
    }
}

/// <summary>Selección de repaso guardada junto al .nc (archivo.nc.repaso.json), para retomarla.</summary>
public sealed class RepasoSelection
{
    public List<int> Selected { get; set; } = new();
    public List<double[]> Zones { get; set; } = new();
    /// <summary>Secciones de trayectorias (índice base 0 y parámetros From/To de RepasoPath).</summary>
    public List<RepasoSection> Sections { get; set; } = new();
    public double DepthMm { get; set; }
    public int PathCount { get; set; }

    public static string PathFor(string ncPath) => ncPath + ".repaso.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save(string ncPath) =>
        File.WriteAllText(PathFor(ncPath), JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Carga la selección guardada si existe y corresponde al mismo número de trayectorias.</summary>
    public static RepasoSelection? Load(string ncPath, int pathCount)
    {
        try
        {
            string p = PathFor(ncPath);
            if (!File.Exists(p)) return null;
            var s = JsonSerializer.Deserialize<RepasoSelection>(File.ReadAllText(p), JsonOptions);
            return s != null && s.PathCount == pathCount ? s : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlyList<RectMm> ZoneRects() =>
        Zones.Where(z => z.Length == 4).Select(z => RectMm.FromCorners(z[0], z[1], z[2], z[3])).ToList();

    /// <summary>Secciones válidas para un programa (trayectoria existente y parámetros dentro de rango).</summary>
    public IReadOnlyList<RepasoSection> SectionsFor(RepasoProgram prog) =>
        Sections.Where(s => s.Path >= 0 && s.Path < prog.Paths.Count &&
                            s.From >= 0 && s.From <= prog.Paths[s.Path].MaxU &&
                            s.To >= 0 && s.To <= prog.Paths[s.Path].MaxU &&
                            Math.Abs(s.From - s.To) > 1e-9).ToList();
}
