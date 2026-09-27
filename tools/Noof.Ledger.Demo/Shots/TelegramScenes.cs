using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Demo.Shots;

internal static class TelegramScenes
{
    static readonly DateOnly Day = new(2026, 9, 18);

    static readonly SystemHealthReport Health = new(HealthLevel.Ok,
    [
        new("Database", HealthLevel.Ok, "ready", MockData.Now, string.Empty),
        new("Telegram", HealthLevel.Ok, "polling", MockData.Now, string.Empty),
        new("AI keys", HealthLevel.Ok, "configured", MockData.Now, string.Empty),
        new("Backup", HealthLevel.Ok, "last success 2 h ago", MockData.Now, string.Empty),
    ]);

    public static IRecordEcho CreateEcho()
    {
        using var services = new ServiceCollection().AddNoofApplication(new SlowOperationOptions(), new FiscalVerificationUrlOptions()).BuildServiceProvider();
        return services.GetRequiredService<IRecordEcho>();
    }

    public static IReadOnlyList<ChatScene> Build(IRecordEcho echo)
    {
        var dinner = Expense("dinner at Walter 45 eur", "Wise", CurrencyCode.Eur, 5657.00m, [Line("Dinner", 45.00m, CurrencyCode.Eur, "Restaurants", "Walter")]);
        var dinnerFixed = dinner with
        {
            Lines = [Line("Dinner", 42.00m, CurrencyCode.Eur, "Restaurants", "Walter")],
            WalletBalances = [new Money(5660.00m, CurrencyCode.Eur)],
        };

        return
        [
            new("expense", "Expense",
            [
                Operator("coffee 350 rsd", "18:30"),
                Reply(echo.Compose(Expense("coffee 350 rsd", "Raiffeisen", CurrencyCode.Rsd, 174150.00m,
                    [Line("Coffee", 350m, CurrencyCode.Rsd, "Coffee", null)])), "18:30"),
            ]),
            new("receipt", "Receipt with a merchant",
            [
                Operator("Lidl groceries 27.80, wine 12.50 eur", "18:31"),
                Reply(echo.Compose(Expense("Lidl groceries 27.80, wine 12.50 eur", "Wise", CurrencyCode.Eur, 5659.70m,
                [
                    Line("Groceries", 27.80m, CurrencyCode.Eur, "Groceries", "Lidl"),
                    Line("Wine", 12.50m, CurrencyCode.Eur, "Groceries", "Lidl"),
                ])), "18:31"),
            ]),
            new("income", "Income",
            [
                Operator("salary 2800 eur", "09:02"),
                Reply(echo.Compose(Expense("salary 2800 eur", "Wise", CurrencyCode.Eur, 5757.20m,
                    [Line("Salary", 2800.00m, CurrencyCode.Eur, "Salary", null)]) with { Kind = TransactionKind.Income }), "09:02"),
            ]),
            new("balance", "Balance statement",
            [
                Operator("wise balance 5700", "20:10"),
                Reply(echo.Compose(Expense("wise balance 5700", "Wise", CurrencyCode.Eur, 5700.00m, []) with
                {
                    Kind = TransactionKind.BalanceCheck,
                    Statement = new BalanceStatement(new Money(5700.00m, CurrencyCode.Eur), 5720.30m),
                }), "20:10"),
            ]),
            new("cancel-restore", "Cancelled, with Restore",
            [
                Operator("taxi 850 rsd", "22:47"),
                Reply(echo.Compose(Expense("taxi 850 rsd", "Raiffeisen", CurrencyCode.Rsd, 175350.00m,
                    [Line("Taxi", 850m, CurrencyCode.Rsd, "Transport", null)]) with { Status = TransactionStatus.Cancelled }), "22:47"),
            ]),
            new("correction", "Fixing a mistake",
            [
                Operator("dinner at Walter 45 eur", "21:05"),
                Reply(echo.Compose(dinnerFixed), "21:05"),
                new(ChatSide.Bot, echo.EditPrompt, "21:06", Quote: echo.Compose(dinnerFixed).Text),
                Operator("no, 42", "21:06") with { Quote = echo.EditPrompt },
            ]),
            new("failure", "A message it could not read",
            [
                Operator("#@%& ???", "11:12"),
                Reply(echo.Failure, "11:12"),
            ]),
            new("health", "/health",
            [
                Operator("/health", "08:00"),
                new(ChatSide.Bot, HealthReplyFormatter.Format(Health), "08:00"),
            ]),
        ];
    }

    static ChatBubble Operator(string text, string time) => new(ChatSide.Operator, text, time);

    static ChatBubble Reply(EchoMessage echo, string time) =>
        new(ChatSide.Bot, echo.Text, time, Edited: true, Buttons: [.. echo.Actions.Select(action => RecordActionButtons.ToButton(action).Text)]);

    static RecordedLine Line(string description, decimal amount, CurrencyCode currency, string category, string? merchant) =>
        new(description, new Money(amount, currency), category.ToLowerInvariant(), category, merchant);

    static CategorizationSubject Expense(string raw, string wallet, CurrencyCode currency, decimal balance, IReadOnlyList<RecordedLine> lines) =>
        new(Guid.Empty, raw, MockData.TelegramChatId, 1, wallet, TransactionStatus.Completed, Day, Day, lines,
            WalletCurrency: currency, WalletBalances: [new Money(balance, currency)]);
}
