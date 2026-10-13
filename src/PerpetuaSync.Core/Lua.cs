using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PerpetuaSync.Core;

/// <summary>
/// Reads the table syntax WoW writes for saved variables, into JSON nodes. It is a parser for data only: it never
/// runs the file, and anything that isn't a literal (a function call, an expression) is an error. Lists of tables
/// keyed 1..n become arrays, everything else objects with string keys, like the Python app (sync/perpetua_sync.py).
/// </summary>
public sealed class LuaReader
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    const int MaxDepth = 200;

    readonly byte[] _s;
    int _i;
    int _depth;

    LuaReader(byte[] text) => _s = text;

    /// <summary>The top-level assignments of a saved-variables file: name -> value.</summary>
    public static Dictionary<string, JsonNode?> ReadAssignments(byte[] text)
    {
        var r = new LuaReader(text);
        var output = new Dictionary<string, JsonNode?>();
        r.Skip();
        while (r._i < r._s.Length)
        {
            var name = r.Word() ?? throw r.Error("expected a name");
            r.Skip();
            r.Expect((byte)'=');
            output[name] = r.Value();
            r.Skip();
        }
        return output;
    }

    public static Dictionary<string, JsonNode?> ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"{info.Name} is larger than {MaxFileBytes / 1024 / 1024} MB");
        return ReadAssignments(File.ReadAllBytes(path));
    }

    FormatException Error(string what) => new($"{what} at byte {_i}");

    void Skip()
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { _i++; continue; }
            if (c == '-' && _i + 1 < _s.Length && _s[_i + 1] == '-')
            {
                while (_i < _s.Length && _s[_i] != '\n') _i++;
                continue;
            }
            break;
        }
    }

    void Expect(byte c)
    {
        Skip();
        if (_i >= _s.Length || _s[_i] != c) throw Error($"expected '{(char)c}'");
        _i++;
    }

    bool Peek(byte c)
    {
        Skip();
        return _i < _s.Length && _s[_i] == c;
    }

    static bool IsWordStart(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_';
    static bool IsWord(byte c) => IsWordStart(c) || c is >= (byte)'0' and <= (byte)'9';

    string? Word()
    {
        Skip();
        if (_i >= _s.Length || !IsWordStart(_s[_i])) return null;
        var start = _i;
        while (_i < _s.Length && IsWord(_s[_i])) _i++;
        return Encoding.ASCII.GetString(_s, start, _i - start);
    }

    JsonNode? Value()
    {
        Skip();
        if (_i >= _s.Length) throw Error("unexpected end of file");
        var c = _s[_i];
        if (c == '"' || c == '\'') return JsonValue.Create(String());
        if (c == '{') return Table();
        if (c == '-' || c == '.' || c is >= (byte)'0' and <= (byte)'9') return Number();
        var w = Word();
        return w switch
        {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            "nil" => null,
            _ => throw Error($"unexpected '{w ?? ((char)c).ToString()}'"),
        };
    }

    JsonNode Number()
    {
        var start = _i;
        if (_s[_i] == '-') _i++;
        while (_i < _s.Length && (_s[_i] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'e' or (byte)'E'
               || ((_s[_i] is (byte)'+' or (byte)'-') && (_s[_i - 1] is (byte)'e' or (byte)'E')))) _i++;
        var tok = Encoding.ASCII.GetString(_s, start, _i - start);
        // Whole numbers written without a point or exponent are integers, like the Python reader.
        if (!tok.Contains('.') && !tok.Contains('e') && !tok.Contains('E') && long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            // 5.0 stays a number with a point, but JSON has no difference; Python turns "5.0" into the float 5.0.
            return JsonValue.Create(d);
        }
        throw Error($"bad number '{tok}'");
    }

    string String()
    {
        var quote = _s[_i++];
        var bytes = new List<byte>();
        while (true)
        {
            if (_i >= _s.Length) throw Error("unterminated string");
            var c = _s[_i++];
            if (c == quote) break;
            if (c != '\\') { bytes.Add(c); continue; }
            if (_i >= _s.Length) throw Error("unterminated string");
            var n = _s[_i];
            if (n is >= (byte)'0' and <= (byte)'9')
            {
                int v = 0, k = 0;
                while (k < 3 && _i < _s.Length && _s[_i] is >= (byte)'0' and <= (byte)'9') { v = v * 10 + (_s[_i] - '0'); _i++; k++; }
                bytes.Add((byte)(v & 255));
                continue;
            }
            _i++;
            if (n is (byte)'\r' or (byte)'\n')
            {
                // An escaped line break is one newline, whichever kind (\r\n, \n\r, \r, \n), as in Lua itself.
                if (_i < _s.Length && _s[_i] is (byte)'\r' or (byte)'\n' && _s[_i] != n) _i++;
                bytes.Add((byte)'\n');
                continue;
            }
            bytes.Add(n switch
            {
                (byte)'n' => (byte)'\n', (byte)'t' => (byte)'\t', (byte)'r' => (byte)'\r', (byte)'a' => 7, (byte)'b' => 8,
                (byte)'f' => 12, (byte)'v' => 11, _ => n, // \\ \" \' and an escaped newline are the character itself
            });
        }
        return new UTF8Encoding(false, false).GetString(bytes.ToArray());
    }

    JsonNode Table()
    {
        if (++_depth > MaxDepth) throw Error("tables nested too deeply");
        _i++; // {
        var items = new List<KeyValuePair<object, JsonNode?>>();
        long seq = 1;
        while (!Peek((byte)'}'))
        {
            if (_i >= _s.Length) throw Error("unterminated table");
            object key;
            if (Peek((byte)'['))
            {
                _i++;
                var k = Value();
                Expect((byte)']');
                Expect((byte)'=');
                key = k is JsonValue kv && kv.TryGetValue<long>(out var lk) ? lk : KeyText(k);
            }
            else
            {
                var save = _i;
                var w = Word();
                if (w != null && Peek((byte)'=') && !(_i + 1 < _s.Length && _s[_i + 1] == '='))
                {
                    _i++;
                    key = w;
                }
                else
                {
                    _i = save;
                    key = seq++;
                }
            }
            items.Add(new(key, Value()));
            Skip();
            if (_i < _s.Length && _s[_i] is (byte)',' or (byte)';') _i++;
        }
        _i++; // }
        _depth--;

        // Tables keyed 1..n are lists.
        if (items.Count > 0 && items.All(p => p.Key is long))
        {
            var byKey = new Dictionary<long, JsonNode?>();
            foreach (var p in items) byKey[(long)p.Key] = p.Value;
            if (byKey.Count == items.Count && Enumerable.Range(1, items.Count).All(n => byKey.ContainsKey(n)))
            {
                var arr = new JsonArray();
                for (long n = 1; n <= items.Count; n++) arr.Add(byKey[n]);
                return arr;
            }
        }
        var obj = new JsonObject();
        foreach (var p in items)
        {
            var k = p.Key is long l ? l.ToString(CultureInfo.InvariantCulture) : (string)p.Key;
            obj[k] = p.Value; // a repeated key: the last one wins, as in Lua
        }
        return obj;
    }

    string KeyText(JsonNode? k) => k switch
    {
        null => throw Error("nil table key"),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False", // what Python's str() gives
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw Error("table used as a key"),
    };
}

/// <summary>
/// Writes data from the site as Lua for the addon's SiteData.lua. The game RUNS that file, so this is the line
/// between "text from the internet" and "code in the game": every string and every key goes out as a quoted Lua
/// string with everything outside printable ASCII escaped, and nothing else is ever written but literals.
/// </summary>
public static class LuaWriter
{
    const int MaxDepth = 64;

    public static string Value(JsonNode? v) { var sb = new StringBuilder(); Write(sb, v, "", 0); return sb.ToString(); }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b == '\n') sb.Append("\\n");
            else if (b == '"' || b == '\\') sb.Append('\\').Append((char)b);
            else if (b >= 32 && b < 127) sb.Append((char)b);
            else sb.Append('\\').Append(b.ToString("D3", CultureInfo.InvariantCulture));
        }
        return sb.Append('"').ToString();
    }

    static void Write(StringBuilder sb, JsonNode? v, string indent, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("site data nested too deeply");
        switch (v)
        {
            case null:
                sb.Append("nil");
                break;
            case JsonValue jv:
                if (jv.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (jv.TryGetValue<string>(out var s)) sb.Append(Quote(s));
                else if (jv.TryGetValue<long>(out var l)) sb.Append(l.ToString(CultureInfo.InvariantCulture));
                else if (jv.TryGetValue<double>(out var d))
                {
                    if (!double.IsFinite(d)) throw new InvalidDataException("number out of range in site data");
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    // A JsonElement number (from parsed JSON): only digits, sign, point and exponent get through.
                    var raw = jv.ToJsonString();
                    if (raw.Length == 0 || raw.Any(ch => !(char.IsAsciiDigit(ch) || ch is '-' or '+' or '.' or 'e' or 'E')))
                        throw new InvalidDataException("unexpected value in site data");
                    sb.Append(raw);
                }
                break;
            case JsonArray arr:
            {
                var inner = indent + "  ";
                sb.Append("{\n");
                foreach (var x in arr) { sb.Append(inner); Write(sb, x, inner, depth + 1); sb.Append(",\n"); }
                sb.Append(indent).Append('}');
                break;
            }
            case JsonObject obj:
            {
                var inner = indent + "  ";
                sb.Append("{\n");
                foreach (var (k, x) in obj)
                {
                    if (x is null) continue;
                    sb.Append(inner).Append('[').Append(Quote(k)).Append("] = ");
                    Write(sb, x, inner, depth + 1);
                    sb.Append(",\n");
                }
                sb.Append(indent).Append('}');
                break;
            }
        }
    }
}
