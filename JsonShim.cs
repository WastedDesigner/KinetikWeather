// .NET 8 has no System.Web.Extensions; this gives the Hub the one call it uses, on System.Text.Json.
using System.Collections.Generic;
using System.Text.Json;

namespace System.Web.Script.Serialization
{
    public class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }

        public object DeserializeObject(string json)
        {
            using (var doc = JsonDocument.Parse(json)) return Convert(doc.RootElement);
        }

        static object Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = Convert(p.Value);
                    return d;
                case JsonValueKind.Array:
                    var l = new List<object>();
                    foreach (var i in e.EnumerateArray()) l.Add(Convert(i));
                    return l.ToArray();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.TryGetInt32(out var n) ? n : e.TryGetInt64(out var m) ? m : (object)e.GetDecimal();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
