namespace Noof.Ledger.Ai.Anthropic;

internal sealed class AnthropicOptions
{
    // Exactly one model property, deliberately. No AdviserModel, no EscalationModel, no
    // FallbackModel: making the type incapable of expressing a tier is stronger than choosing
    // not to configure one. A pinned dated id, not a floating alias, so a model change is a
    // commit rather than a surprise.
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    public int MaxTokens { get; init; } = 2048;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    // There is no Temperature property and there must not be one. Microsoft.Extensions.AI's
    // ChatOptions.Temperature exists and would compile, unlike the raw SDK's
    // MessageCreateParams.Temperature (which is [Obsolete] and a compile error under
    // TreatWarningsAsErrors) — but it is never set either. Determinism here comes from the forced
    // strict tool call's schema, not from a sampling parameter, and giving this options type a
    // Temperature property would invite someone to "tune" a call that is supposed to be
    // deterministic by construction.
}
