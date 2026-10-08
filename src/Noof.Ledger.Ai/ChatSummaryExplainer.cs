using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Ai.SummaryExplainerLogging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Reporting.Summary;

namespace Noof.Ledger.Ai;

internal sealed class ChatSummaryExplainer(
    IChatClientFactory clientFactory, IFiscalVerificationUrl verificationUrl, IOperationTimer timer,
    ILogger<ChatSummaryExplainer> logger) : ISummaryExplainer
{
    const string WriteSummaryCommentName = "write_summary_comment";
    const string WriteSummaryCommentDescription =
        "Write a short English comment on the monthly spending summary for the person who keeps it.";

    public async Task<string> ExplainAsync(SummaryExplanationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await AskAsync(StripLines(request.SummaryText), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.SummaryCommentFailed(FailureType(exception));
            throw;
        }
    }

    async Task<string> AskAsync(string summaryText, CancellationToken cancellationToken)
    {
        using var timing = timer.Start(logger, TimedOperations.ModelExplainSummary);

        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, summaryText)],
            new ChatOptions
            {
                Instructions = SummaryCommentPrompt.System,
                Tools = [new SchemaTool(WriteSummaryCommentName, WriteSummaryCommentDescription, SummaryCommentSchema.WriteSummaryComment)],
                ToolMode = ChatToolMode.RequireSpecific(WriteSummaryCommentName),
            },
            cancellationToken);

        if (FindCall(response) is not { } call)
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{WriteSummaryCommentName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var text = ToPayload(call)?.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new ModelCallException(ModelFailureKind.Transient, $"{WriteSummaryCommentName} returned no text.");

        return text.Trim();
    }

    // The summary is composed by C#, but its merchant names and line descriptions came from receipts and messages, so a
    // verification URL must not reach the model through them (CLAUDE.md §4 Secrets). Line by line, so a removed link
    // never joins two lines: StripUrl also takes the line break touching a link.
    string StripLines(string text) =>
        string.Join('\n', text.Split('\n').Select(line => verificationUrl.StripUrl(line) ?? ""));

    static string FailureType(Exception exception) =>
        exception is ModelCallException modelFailure ? modelFailure.Kind.ToString() : exception.GetType().Name;

    static FunctionCallContent? FindCall(ChatResponse response) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == WriteSummaryCommentName);

    static WriteSummaryCommentPayload? ToPayload(FunctionCallContent call) =>
        JsonSerializer.Deserialize<WriteSummaryCommentPayload>(JsonSerializer.SerializeToElement(call.Arguments));

    sealed record WriteSummaryCommentPayload([property: JsonPropertyName("text")] string? Text);
}
