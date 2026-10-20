using System.Globalization;

namespace Tostatronic.Cnc.Cam.Gerber;

/// <summary>Un barreno: centro en mm y diámetro de la broca que pide el diseño.</summary>
public readonly record struct DrillHole(double X, double Y, double Diameter);

/// <summary>
/// Parser de Excellon (el formato de barrenos que acompaña a los Gerber): cabecera M48 con
/// METRIC/INCH y definiciones TnC, cuerpo con selección de herramienta y coordenadas X/Y
/// (con punto decimal o con decimales implícitos). Los slots G85 se aproximan con un barreno
/// en cada extremo.
/// </summary>
public static class ExcellonParser
{
    public static List<DrillHole> Parse(string text, List<string>? warnings = null)
    {
        var holes = new List<DrillHole>();
        var tools = new Dictionary<int, double>();
        warnings ??= new List<string>();
        double unit = 1.0;          // 1 = mm
        bool leadingZeros = true;   // LZ: ceros a la izquierda presentes → decimales implícitos a la derecha
        int intDigits = 3, decimals = 3;
        bool inHeader = false;
        int currentTool = 0;
        double lastX = 0, lastY = 0;
        bool hasLast = false;

        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;

            if (line == "M48") { inHeader = true; continue; }
            if (line == "%" || line == "M95") { inHeader = false; continue; }
            if (line.StartsWith("M30") || line.StartsWith("M00")) break;

            if (line.StartsWith("METRIC") || line.StartsWith("M71"))
            {
                unit = 1.0;
                if (line.Contains("TZ")) leadingZeros = false;
                SetFormat(line, ref intDigits, ref decimals, 3, 3);
                continue;
            }
            if (line.StartsWith("INCH") || line.StartsWith("M72"))
            {
                unit = 25.4;
                if (line.Contains("TZ")) leadingZeros = false;
                SetFormat(line, ref intDigits, ref decimals, 2, 4);
                continue;
            }

            if (line.StartsWith('T'))
            {
                int i = 1;
                while (i < line.Length && char.IsDigit(line[i])) i++;
                if (i == 1) continue;
                int tn = int.Parse(line[1..i]);
                int ci = line.IndexOf('C', i);
                if (inHeader && ci >= 0)
                {
                    int j = ci + 1;
                    while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.')) j++;
                    tools[tn] = double.Parse(line[(ci + 1)..j], CultureInfo.InvariantCulture) * unit;
                }
                else
                {
                    currentTool = tn;
                }
                continue;
            }

            if (inHeader) continue;

            if (line.StartsWith('X') || line.StartsWith('Y'))
            {
                var (x, y, ok) = ParseXY(line, unit, leadingZeros, intDigits, decimals,
                                         hasLast ? lastX : 0, hasLast ? lastY : 0, out bool slot, out double x2, out double y2);
                if (!ok) continue;
                double diam = tools.TryGetValue(currentTool, out double d) ? d : 1.0;
                if (!tools.ContainsKey(currentTool))
                    AddOnce(warnings, $"Herramienta T{currentTool} sin diámetro definido: se asume 1.0 mm.");
                holes.Add(new DrillHole(x, y, diam));
                if (slot)
                {
                    holes.Add(new DrillHole(x2, y2, diam));
                    AddOnce(warnings, "Slot G85 aproximado con un barreno en cada extremo.");
                }
                lastX = slot ? x2 : x;
                lastY = slot ? y2 : y;
                hasLast = true;
            }
            // G90/G05/G81 y demás: se ignoran
        }
        return holes;
    }

    private static void AddOnce(List<string> warnings, string msg)
    {
        if (!warnings.Contains(msg)) warnings.Add(msg);
    }

    private static void SetFormat(string line, ref int intDigits, ref int decimals, int defInt, int defDec)
    {
        // METRIC,LZ,000.000 → formato explícito opcional
        int dot = line.IndexOf('.');
        if (dot > 0)
        {
            int i0 = dot;
            while (i0 > 0 && line[i0 - 1] == '0') i0--;
            int i1 = dot + 1;
            while (i1 < line.Length && line[i1] == '0') i1++;
            intDigits = dot - i0;
            decimals = i1 - dot - 1;
            if (intDigits == 0 || decimals == 0) { intDigits = defInt; decimals = defDec; }
        }
        else
        {
            intDigits = defInt;
            decimals = defDec;
        }
    }

    private static (double X, double Y, bool Ok) ParseXY(string line, double unit, bool leadingZeros,
        int intDigits, int decimals, double lastX, double lastY, out bool slot, out double x2, out double y2)
    {
        slot = false; x2 = 0; y2 = 0;
        double x = lastX, y = lastY;
        bool any = false;
        int k = 0;
        bool second = false;
        double sx = lastX, sy = lastY;
        while (k < line.Length)
        {
            char key = line[k];
            if (key == 'G' && line.Length - k >= 3 && line[k + 1] == '8' && line[k + 2] == '5')
            {
                slot = true; second = true; k += 3; continue;
            }
            if (key is not ('X' or 'Y')) { k++; continue; }
            k++;
            int start = k;
            while (k < line.Length && (char.IsDigit(line[k]) || line[k] is '+' or '-' or '.')) k++;
            string num = line[start..k];
            if (num.Length == 0) continue;
            double val = ParseNum(num, unit, leadingZeros, intDigits, decimals);
            if (!second)
            {
                if (key == 'X') x = val; else y = val;
                any = true;
            }
            else
            {
                if (key == 'X') sx = val; else sy = val;
            }
        }
        if (slot) { x2 = sx; y2 = sy; }
        return (x, y, any || slot);
    }

    private static double ParseNum(string s, double unit, bool leadingZeros, int intDigits, int decimals)
    {
        if (s.Contains('.'))
            return double.Parse(s, CultureInfo.InvariantCulture) * unit;
        bool neg = s.StartsWith('-');
        string digits = s.TrimStart('+', '-');
        if (!leadingZeros) // TZ: faltan ceros a la derecha
            digits = digits.PadRight(intDigits + decimals, '0');
        double v = long.Parse(digits) / Math.Pow(10, decimals) * unit;
        return neg ? -v : v;
    }
}
