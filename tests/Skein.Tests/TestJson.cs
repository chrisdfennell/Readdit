using System.Text.Json;

namespace Skein.Tests;

static class TestJson
{
    /// <summary>Parses JSON into a standalone element (cloned, so the document can go).</summary>
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>A t3 listing child with the given data object.</summary>
    public static JsonElement Thing(string kind, string data) => Parse($$"""{"kind":"{{kind}}","data":{{data}}}""");
}
