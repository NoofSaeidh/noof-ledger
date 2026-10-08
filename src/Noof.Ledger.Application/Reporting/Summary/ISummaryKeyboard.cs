using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Application.Reporting.Summary;

// The bot's buttons under a summary, so Host can send one without naming Telegram's callback vocabulary.
public interface ISummaryKeyboard
{
    IReadOnlyList<ChatButton> For(MonthlySummary summary, DateOnly currentMonth);
}
