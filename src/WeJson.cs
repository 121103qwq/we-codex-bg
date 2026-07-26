// WeJson.cs -- a small, dependency-free JSON reader.
//
// project.json needs real parsing, not string scraping: the wallpaper properties
// live at general.properties.<name>.{type,text,value,options[]} and descriptions
// routinely contain quotes, newlines and \uXXXX escapes.  .NET Framework has no
// JSON type in mscorlib, and the project deliberately builds with nothing but the
// stock csc.exe, so this is the parser.
//
// Read-only, ~200 lines, no reflection, no allocations beyond the tree itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

internal enum JKind { Null, Bool, Number, String, Array, Object }

internal sealed class JVal
{
    public JKind Kind = JKind.Null;
    public bool Bool;
    public double Number;
    public string Str = "";
    public List<JVal> Items;                       // Array
    public Dictionary<string, JVal> Members;       // Object (insertion-ordered via Order)
    public List<string> Order;                     // preserves the file's key order

    public static JVal Parse(string text)
    {
        int i = 0;
        JVal v = ParseValue(text, ref i);
        return v;
    }

    // ---- convenience accessors; every one is null/'' safe ----

    public JVal this[string key]
    {
        get
        {
            JVal v;
            if (Kind == JKind.Object && Members != null && Members.TryGetValue(key, out v)) return v;
            return null;
        }
    }
    public string AsString(string fallback = "")
    {
        switch (Kind)
        {
            case JKind.String: return Str;
            case JKind.Number: return Number.ToString(CultureInfo.InvariantCulture);
            case JKind.Bool: return Bool ? "true" : "false";
            default: return fallback;
        }
    }
    public double AsNumber(double fallback = 0)
    {
        if (Kind == JKind.Number) return Number;
        double d;
        if (Kind == JKind.String &&
            double.TryParse(Str, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return fallback;
    }
    public bool AsBool(bool fallback = false)
    {
        if (Kind == JKind.Bool) return Bool;
        if (Kind == JKind.Number) return Number != 0;
        if (Kind == JKind.String) return Str == "true" || Str == "1";
        return fallback;
    }
    public IEnumerable<string> Keys
    {
        get { return Order ?? (IEnumerable<string>)new string[0]; }
    }
    public IEnumerable<JVal> Array
    {
        get { return Items ?? (IEnumerable<JVal>)new JVal[0]; }
    }

    // ---- parser ----

    static void Ws(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }

    static JVal ParseValue(string s, ref int i)
    {
        Ws(s, ref i);
        if (i >= s.Length) return new JVal();
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return new JVal { Kind = JKind.String, Str = ParseString(s, ref i) };
            case 't':
                if (i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return new JVal { Kind = JKind.Bool, Bool = true }; }
                break;
            case 'f':
                if (i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return new JVal { Kind = JKind.Bool, Bool = false }; }
                break;
            case 'n':
                if (i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return new JVal(); }
                break;
        }
        return ParseNumber(s, ref i);
    }

    static JVal ParseObject(string s, ref int i)
    {
        var o = new JVal
        {
            Kind = JKind.Object,
            Members = new Dictionary<string, JVal>(StringComparer.Ordinal),
            Order = new List<string>()
        };
        i++;                                   // '{'
        Ws(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return o; }
        while (i < s.Length)
        {
            Ws(s, ref i);
            if (i >= s.Length || s[i] != '"') break;
            string key = ParseString(s, ref i);
            Ws(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            JVal v = ParseValue(s, ref i);
            if (!o.Members.ContainsKey(key)) o.Order.Add(key);
            o.Members[key] = v;
            Ws(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; break; }
            break;
        }
        return o;
    }

    static JVal ParseArray(string s, ref int i)
    {
        var a = new JVal { Kind = JKind.Array, Items = new List<JVal>() };
        i++;                                   // '['
        Ws(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (i < s.Length)
        {
            a.Items.Add(ParseValue(s, ref i));
            Ws(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; break; }
            break;
        }
        return a;
    }

    static string ParseString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;                                   // opening quote
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"') { i++; break; }
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == 'u' && i + 5 < s.Length)
                {
                    int code;
                    if (int.TryParse(s.Substring(i + 2, 4), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out code))
                    { sb.Append((char)code); i += 6; continue; }
                }
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    default: sb.Append(n); break;
                }
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    static JVal ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        double d;
        if (i > start && double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out d))
            return new JVal { Kind = JKind.Number, Number = d };
        if (i == start) i++;                   // never stall on garbage
        return new JVal();
    }

    // ---- writing (only what applyProperties needs) ----

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
