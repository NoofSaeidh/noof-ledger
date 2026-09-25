using AwesomeAssertions;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

public class ExceptionReasonTests
{
    [Fact]
    public void Null_or_empty_exception_text_yields_no_reason()
    {
        ExceptionReason.Extract(null).Should().BeNull();
        ExceptionReason.Extract("").Should().BeNull();
        ExceptionReason.Extract("   ").Should().BeNull();
    }

    [Fact]
    public void An_Anthropic_style_error_body_yields_the_API_error_message()
    {
        const string text = """
            Noof.Ledger.Application.Categorization.ModelCallException: model call failed ---> Noof.Ledger.Ai.Anthropic.AnthropicBadRequestException: {"type":"error","error":{"type":"invalid_request_error","message":"tools.1.custom: Invalid schema: expected required to list every property"},"request_id":"req_01AbCdEfGhIjKlMnOpQrStUv"}
               at Noof.Ledger.Ai.Anthropic.AnthropicTranslatingChatClient.GetResponseAsync(IEnumerable`1 messages, ChatOptions options, CancellationToken cancellationToken)
               --- End of inner exception stack trace ---
               at Noof.Ledger.Ai.ChatCategorizer.CategorizeAsync(CategorizationRequest request, CancellationToken cancellationToken)
            """;

        ExceptionReason.Extract(text).Should()
            .Be("tools.1.custom: Invalid schema: expected required to list every property");
    }

    [Fact]
    public void An_error_message_with_escaped_quotes_and_newlines_is_decoded()
    {
        const string text = """
            {"type":"error","error":{"type":"invalid_request_error","message":"unexpected \"field\"\nsecond line"},"request_id":"req_1"}
            """;

        ExceptionReason.Extract(text).Should().Be("unexpected \"field\"\nsecond line");
    }

    [Fact]
    public void With_no_recognisable_API_error_body_the_first_line_of_the_exception_is_the_reason()
    {
        const string text = """
            System.TimeoutException: The operation has timed out.
               at Noof.Ledger.Ai.Groq.GroqSpeechToTextClient.TranscribeAsync(CapturedVoice voice, CancellationToken cancellationToken)
            """;

        ExceptionReason.Extract(text).Should().Be("System.TimeoutException: The operation has timed out.");
    }

    [Fact]
    public void Leading_and_trailing_whitespace_on_the_first_line_is_trimmed()
    {
        ExceptionReason.Extract("  boom  \nmore text").Should().Be("boom");
    }
}
