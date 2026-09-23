namespace Noof.Ledger.Ai.Tests;

// Real response shapes, kept in one place so every test reads from the same ground truth instead
// of each hand-rolling its own JSON. record_spending, canonicalize_merchant and list_merchants are
// now all genuine "tool_use" blocks, forced by tool_choice (operator, 2026-09-23).
public static class AnthropicResponses
{
    public const string RecordSpendingJsonAnswer = """
        {"id":"msg_01","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_01","name":"record_spending","input":{"items":[{"description":"Coffee","amount":3.50,"currency":"EUR","category_slug":"food-drink","merchant_name":null}]}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string ListMerchantsToolUse = """
        {"id":"msg_02","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_02","name":"list_merchants","input":{}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":80,"output_tokens":10}}
        """;

    public const string CanonicalizeMerchantJsonAnswer = """
        {"id":"msg_03","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_03","name":"canonicalize_merchant","input":{"display_name":"Lidl"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":40,"output_tokens":8}}
        """;

    public const string RecordSpendingFromWordsAnswer = """
        {"id":"msg_05","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_05","name":"record_spending","input":{"items":[{"description":"продукты","amount":1000,"currency":"EUR","category_slug":"food-drink","merchant_name":"Lidl"}]}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string NoAnswerAtAll = """
        {"id":"msg_04","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":50,"output_tokens":0}}
        """;

    public const string AuthenticationError = """
        {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}
        """;

    public static string GenericError(string type, string message) =>
        $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""";
}
