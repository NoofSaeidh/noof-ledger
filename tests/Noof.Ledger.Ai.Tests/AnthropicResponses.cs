namespace Noof.Ledger.Ai.Tests;

// Real response shapes, kept in one place so every test reads from the same ground truth instead
// of each hand-rolling its own JSON. record_transaction, canonicalize_merchant and list_merchants
// are all genuine "tool_use" blocks, forced by tool_choice (operator, 2026-09-23; renamed for M9,
// 2026-09-24).
public static class AnthropicResponses
{
    public const string RecordTransactionJsonAnswer = """
        {"id":"msg_01","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_01","name":"record_transaction","input":{"items":[{"description":"Coffee","amount":3.50,"currency":"EUR","category_slug":"food-drink","merchant_name":null}],"occurred_on":null,"kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
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

    public const string RecordTransactionFromWordsAnswer = """
        {"id":"msg_05","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_05","name":"record_transaction","input":{"items":[{"description":"продукты","amount":1000,"currency":"EUR","category_slug":"food-drink","merchant_name":"Lidl"}],"occurred_on":null,"kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string RecordTransactionWithDateAnswer = """
        {"id":"msg_06","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_06","name":"record_transaction","input":{"items":[{"description":"продукты","amount":1000,"currency":"EUR","category_slug":"food-drink","merchant_name":null}],"occurred_on":"2026-09-21","kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string NoAnswerAtAll = """
        {"id":"msg_04","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":50,"output_tokens":0}}
        """;

    public const string ReadReceiptJsonAnswer = """
        {"id":"msg_08","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_08","name":"read_receipt","input":{"seller_name":"Maxi","seller_tax_id":"123456789","issued_at":"2026-09-20T14:05:00","currency":"RSD","total":845.50,"payment_method":"card","kind":"sale","lines":[{"name":"Mleko","quantity":1,"unit_price":120,"total":120},{"name":"Hleb","quantity":2,"unit_price":90.25,"total":180.50}]}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":200,"output_tokens":40}}
        """;

    public const string CategorizeReceiptJsonAnswer = """
        {"id":"msg_09","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_09","name":"categorize_receipt","input":{"lines":[{"ordinal":1,"category_slug":"groceries"},{"ordinal":2,"category_slug":"groceries"}],"merchant_name":"Maxi","wallet_id":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":150,"output_tokens":20}}
        """;

    public const string AuthenticationError = """
        {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}
        """;

    public static string GenericError(string type, string message) =>
        $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""";
}
