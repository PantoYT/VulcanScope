using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VulcanScope;

/// <summary>
/// Small, null-safe helpers for navigating JsonNode trees. Numeric reads use TryGetValue
/// across types so they work for both network-parsed values (JsonElement-backed) and values
/// we construct in code from int/double (JsonValue&lt;T&gt;-backed).
/// </summary>
public static class J
{
    public static JsonNode? Nav(JsonNode? n, params string[] path)
    {
        foreach (var p in path)
        {
            if (n is JsonObject o)
                n = o.ContainsKey(p) ? o[p] : null;
            else
                return null;
        }
        return n;
    }

    public static double? Num(this JsonNode? n, params string[] path)
    {
        var v = path.Length == 0 ? n : Nav(n, path);
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.Number) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    public static int? Int(this JsonNode? n, params string[] path)
    {
        var d = n.Num(path);
        return d.HasValue ? (int)d.Value : null;
    }

    public static string Str(this JsonNode? n, params string[] path)
    {
        var v = path.Length == 0 ? n : Nav(n, path);
        if (v is null) return "";
        var k = v.GetValueKind();
        if (k == JsonValueKind.String && v is JsonValue sv && sv.TryGetValue<string>(out var s)) return s;
        if (k == JsonValueKind.Number)
        {
            var d = v.Num();
            return d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "";
        }
        return "";
    }

    public static bool Bool(this JsonNode? n, params string[] path)
    {
        var v = path.Length == 0 ? n : Nav(n, path);
        return v != null && v.GetValueKind() == JsonValueKind.True;
    }

    public static JsonArray Arr(this JsonNode? n) => n as JsonArray ?? new JsonArray();

    public static JsonNode Clone(JsonNode n) => JsonNode.Parse(n.ToJsonString())!;
}
