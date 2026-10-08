using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noof.Ledger.Ai;

internal static class FindingExplanationSchema
{
    public static JsonElement WriteExplanation { get; } = BuildWriteExplanation();

    static JsonElement BuildWriteExplanation()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("text", "looks_like_bug"),
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "The explanation for the operator, in English, plain text: what is wrong, the likely cause, and what to do.",
                },
                ["looks_like_bug"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "true only when the likely cause is the app's own code rather than the operator's data or a misread phrase.",
                },
            },
        };

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
