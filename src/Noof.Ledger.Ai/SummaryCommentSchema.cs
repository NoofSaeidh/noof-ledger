using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noof.Ledger.Ai;

internal static class SummaryCommentSchema
{
    public static JsonElement WriteSummaryComment { get; } = BuildWriteSummaryComment();

    static JsonElement BuildWriteSummaryComment()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("text"),
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "The comment, in English, plain text: three to five short sentences on what stands out in the summary.",
                },
            },
        };

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
