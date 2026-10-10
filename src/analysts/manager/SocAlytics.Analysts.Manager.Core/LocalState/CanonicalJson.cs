using System.Text;
using System.Text.Json.Nodes;

namespace SocAlytics.Analysts.Manager.LocalState;

/// <summary>UTF-8 JSON with sorted property names and no insignificant whitespace.</summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonNode node) => Encoding.UTF8.GetBytes(Sort(node)?.ToJsonString() ?? "null");

    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = Sort(pair.Value?.DeepClone());
                }

                return sorted;
            case JsonArray array:
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(Sort(item?.DeepClone()));
                }

                return copy;
            default:
                return node?.DeepClone();
        }
    }
}
