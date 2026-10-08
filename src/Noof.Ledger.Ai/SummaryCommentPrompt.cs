namespace Noof.Ledger.Ai;

internal static class SummaryCommentPrompt
{
    // Every figure in the summary was computed and formatted by C#; the model only comments on them (CLAUDE.md §4
    // Money), and its own figures are not checked (spec §5).
    public const string System = """
        You comment on a person's monthly spending summary. Write three to five short English sentences about what stands
        out: the biggest changes, anything unusual, anything worth watching next month. Use only the figures given. Do not
        give financial advice and do not repeat the whole summary. Answer with the write_summary_comment tool.
        """;
}
