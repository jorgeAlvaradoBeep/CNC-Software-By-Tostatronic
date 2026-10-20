using System.Globalization;

namespace Tostatronic.Cnc.Cam.Gerber;

/// <summary>
/// Evaluador de las expresiones aritméticas de las macros de apertura Gerber:
/// números, variables $n, operadores + − x (multiplicar) / y paréntesis.
/// </summary>
internal static class MacroExpr
{
    public static double Eval(string text, IReadOnlyDictionary<int, double> vars)
    {
        int pos = 0;
        double v = ParseExpr(text, ref pos, vars);
        return v;
    }

    private static double ParseExpr(string s, ref int i, IReadOnlyDictionary<int, double> vars)
    {
        double v = ParseTerm(s, ref i, vars);
        while (true)
        {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '+') { i++; v += ParseTerm(s, ref i, vars); }
            else if (i < s.Length && s[i] == '-') { i++; v -= ParseTerm(s, ref i, vars); }
            else return v;
        }
    }

    private static double ParseTerm(string s, ref int i, IReadOnlyDictionary<int, double> vars)
    {
        double v = ParseFactor(s, ref i, vars);
        while (true)
        {
            SkipSpace(s, ref i);
            if (i < s.Length && (s[i] == 'x' || s[i] == 'X')) { i++; v *= ParseFactor(s, ref i, vars); }
            else if (i < s.Length && s[i] == '/') { i++; v /= ParseFactor(s, ref i, vars); }
            else return v;
        }
    }

    private static double ParseFactor(string s, ref int i, IReadOnlyDictionary<int, double> vars)
    {
        SkipSpace(s, ref i);
        if (i >= s.Length) throw new FormatException($"Expresión incompleta: '{s}'");
        char c = s[i];
        if (c == '-') { i++; return -ParseFactor(s, ref i, vars); }
        if (c == '+') { i++; return ParseFactor(s, ref i, vars); }
        if (c == '(')
        {
            i++;
            double v = ParseExpr(s, ref i, vars);
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ')') i++;
            return v;
        }
        if (c == '$')
        {
            i++;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            int n = int.Parse(s[start..i]);
            return vars.TryGetValue(n, out double v) ? v : 0;
        }
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i == start) throw new FormatException($"Símbolo inesperado '{c}' en '{s}'");
            return double.Parse(s[start..i], CultureInfo.InvariantCulture);
        }
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
