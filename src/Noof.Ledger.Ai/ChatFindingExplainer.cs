using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Ai;

internal sealed class ChatFindingExplainer(
    IChatClientFactory clientFactory, IFiscalVerificationUrl verificationUrl, IOperationTimer timer,
    ILogger<ChatFindingExplainer> logger) : IFindingExplainer
{
    const string WriteExplanationName = "write_explanation";
    const string WriteExplanationDescription =
        "Write a short English explanation of the integrity findings for the operator.";

    public async Task<Explanation> ExplainAsync(ExplanationRequest request, CancellationToken cancellationToken)
    {
        using var timing = timer.Start(logger, TimedOperations.ModelExplainFinding);

        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, FindingExplanationPrompt.BuildUserTurn(Stripped(request)))],
            new ChatOptions
            {
                Instructions = FindingExplanationPrompt.System,
                Tools = [new SchemaTool(WriteExplanationName, WriteExplanationDescription, FindingExplanationSchema.WriteExplanation)],
                ToolMode = ChatToolMode.RequireSpecific(WriteExplanationName),
            },
            cancellationToken);

        if (FindCall(response) is not { } call)
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{WriteExplanationName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var payload = ToPayload(call)
            ?? throw new ModelCallException(ModelFailureKind.Transient, $"{WriteExplanationName} returned an empty payload.");

        if (string.IsNullOrWhiteSpace(payload.Text))
            throw new ModelCallException(ModelFailureKind.Transient, $"{WriteExplanationName} returned a blank text.");

        return new Explanation(payload.Text.Trim(), payload.LooksLikeBug);
    }

    // Stripped here even though the bug-report store strips what it saves: a request composed anywhere - the
    // dashboard's live findings included - must not carry a verification URL to the model (CLAUDE.md §4 Secrets).
    // Line by line, so a removed link never joins two lines: StripUrl also takes the line break touching a link.
    ExplanationRequest Stripped(ExplanationRequest request) => new(
        StripLines(request.Findings),
        StripLines(request.OperatorText),
        StripLines(request.RecordSummary));

    [return: NotNullIfNotNull(nameof(text))]
    string? StripLines(string? text) =>
        text is null ? null : string.Join('\n', text.Split('\n').Select(line => verificationUrl.StripUrl(line) ?? ""));

    static FunctionCallContent? FindCall(ChatResponse response) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == WriteExplanationName);

    static WriteExplanationPayload? ToPayload(FunctionCallContent call) =>
        JsonSerializer.Deserialize<WriteExplanationPayload>(JsonSerializer.SerializeToElement(call.Arguments));

    sealed record WriteExplanationPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("looks_like_bug")] bool LooksLikeBug);
}
