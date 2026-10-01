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

    // The host's own appsettings.json value; the echo only needs it to be a valid prefix.
    static readonly FiscalVerificationUrlOptions FiscalLinks = new() { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" };

    public static IRecordEcho CreateEcho()
    {
        using var services = new ServiceCollection().AddNoofApplication(new SlowOperationOptions(), FiscalLinks).BuildServiceProvider();
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
            new("multi-line", "Several items with a merchant",
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
            new("transfer", "A cash withdrawal",
            [
                Operator("withdrew 10000 rsd from raif", "13:15"),
                Reply(echo.Compose(TransferRecord("withdrew 10000 rsd from raif", Legs(
                    "Raiffeisen", new Money(10000.00m, CurrencyCode.Rsd), 164150.00m,
                    "Cash RSD", new Money(10000.00m, CurrencyCode.Rsd), 12000.00m))), "13:15"),
            ]),
            new("transfer-fee", "A withdrawal with a fee",
            [
                Operator("withdrew 10000 from raif, fee 150", "13:40"),
                Reply(echo.Compose(TransferRecord("withdrew 10000 from raif, fee 150", Legs(
                    "Raiffeisen", new Money(10150.00m, CurrencyCode.Rsd), 154000.00m,
                    "Cash RSD", new Money(10000.00m, CurrencyCode.Rsd), 22000.00m,
                    new Money(150.00m, CurrencyCode.Rsd), TransferLeg.From),
                    [FeeLine("Fee", 150.00m, CurrencyCode.Rsd)])), "13:40"),
            ]),
            new("exchange", "A currency exchange",
            [
                Operator("exchanged 100 eur for 11700 dinars", "14:05"),
                Reply(echo.Compose(TransferRecord("exchanged 100 eur for 11700 dinars", Legs(
                    "Cash EUR", new Money(100.00m, CurrencyCode.Eur), 250.00m,
                    "Cash RSD", new Money(11700.00m, CurrencyCode.Rsd), 33700.00m))), "14:05"),
            ]),
            new("foreign-spending", "Spending in dollars, charged to a tenge wallet at its own rate",
            [
                Operator("taxi 30 dollars from kaspi", "16:20"),
                Reply(echo.Compose(Expense("taxi 30 dollars from kaspi", "Kaspi", CurrencyCode.Kzt, 169744.00m,
                [
                    Line("Taxi", 30.00m, CurrencyCode.Usd, "Transport", null),
                    FeeLine("Fee · USD purchase", 156.00m, CurrencyCode.Kzt),
                ]) with
                {
                    Charges =
                    [
                        new ChargeView(CurrencyCode.Usd, 30.00m, new Money(15600.00m, CurrencyCode.Kzt), new Money(156.00m, CurrencyCode.Kzt),
                            520m, new FeeTerms(1m, null, null), ChargeSource.WalletTerms),
                    ],
                }), "16:20"),
            ]),
            new("no-terms", "A currency the wallet has no rate for",
            [
                Operator("museum 20 eur from kaspi", "17:10"),
                Reply(echo.Compose(Expense("museum 20 eur from kaspi", "Kaspi", CurrencyCode.Kzt, 185500.00m,
                    [Line("Museum", 20.00m, CurrencyCode.Eur, "Entertainment", null)]) with
                {
                    WalletBalances = [new Money(185500.00m, CurrencyCode.Kzt), new Money(-20.00m, CurrencyCode.Eur)],
                }), "17:10"),
            ]),
            new("exchange-question", "An exchange with no amount received",
            [
                Operator("exchanged 100 eur for dinars", "10:30"),
                Reply(echo.Compose(Expense("exchanged 100 eur for dinars", string.Empty, CurrencyCode.Rsd, 0m, []) with
                {
                    Status = TransactionStatus.Failed,
                    Kind = TransactionKind.Transfer,
                    WalletCurrency = null,
                    WalletBalances = null,
                    FailureReason = RecordFailureReason.MissingReceivedAmount,
                }), "10:30"),
            ]),
            ReceiptScene("receipt-qr", "A receipt photo, read from its fiscal QR", echo, MockData.ReceiptTransactionId, "17:42", 172096.06m),
            ReceiptScene("receipt-vision", "The tax site was down, so the lines were read from the photo", echo,
                MockData.VisionReceiptTransactionId, "12:05", 174661.00m),
            new("receipt-check", "A receipt that doesn't add up",
            [
                Photo("09:20"),
                Reply(echo.ComposeReceiptNeedsConfirmation(View(MockData.UnconfirmedReceiptTransactionId)), "09:20"),
            ]),
            new("health", "/health",
            [
                Operator("/health", "08:00"),
                new(ChatSide.Bot, HealthReplyFormatter.Format(Health), "08:00"),
            ]),
        ];
    }

    static ChatBubble Operator(string text, string time) => new(ChatSide.Operator, text, time);

    static ChatBubble Photo(string time) => new(ChatSide.Operator, string.Empty, time, Photo: true);

    // The same receipt the demo database holds, so the picture and the Transactions page agree.
    static ChatScene ReceiptScene(string name, string title, IRecordEcho echo, Guid id, string time, decimal balanceAfter)
    {
        var record = MockData.Records.Single(candidate => candidate.Id == id);
        var receipt = record.Receipt!;
        var subject = new CategorizationSubject(id, string.Empty, MockData.TelegramChatId, 1, record.Wallet!, record.Status, record.Day, record.Day,
            [.. receipt.Lines.Select(line => new RecordedLine(
                line.Name, new Money(line.Total, CurrencyCode.Rsd), line.CategorySlug, CategoryNames[line.CategorySlug!], receipt.SellerName))],
            CaptureKind.Photo, WalletCurrency: CurrencyCode.Rsd, WalletBalances: [new Money(balanceAfter, CurrencyCode.Rsd)]);

        return new(name, title, [Photo(time), Reply(echo.ComposeReceipt(subject, View(id)), time)]);
    }

    static ReceiptView View(Guid id)
    {
        var receipt = MockData.Records.Single(record => record.Id == id).Receipt!;
        return new(id, receipt.Source, receipt.SellerTaxId, receipt.SellerName, receipt.SellerAddress, receipt.LocationName,
            receipt.FiscalNumber, receipt.IssuedAt, receipt.Total, CurrencyCode.Rsd, ReceiptKind.Sale, receipt.Payment, receipt.QrTotal,
            VerificationUrl: null,
            [.. receipt.Lines.Select((line, index) => new ReceiptLineView(
                id, index + 1, line.Name, line.Quantity, line.Unit, line.UnitPrice, line.Total, TaxLabel: null))]);
    }

    static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["groceries"] = "Groceries",
        ["personal-care"] = "Personal Care",
        ["health"] = "Health",
    };

    static ChatBubble Reply(EchoMessage echo, string time) =>
        new(ChatSide.Bot, echo.Text, time, Edited: true, Buttons: [.. echo.Actions.Select(action => RecordActionButtons.ToButton(action).Text)]);

    static RecordedLine Line(string description, decimal amount, CurrencyCode currency, string category, string? merchant) =>
        new(description, new Money(amount, currency), category.ToLowerInvariant(), category, merchant);

    static CategorizationSubject Expense(string raw, string wallet, CurrencyCode currency, decimal balance, IReadOnlyList<RecordedLine> lines) =>
        new(Guid.Empty, raw, MockData.TelegramChatId, 1, wallet, TransactionStatus.Completed, Day, Day, lines,
            WalletCurrency: currency, WalletBalances: [new Money(balance, currency)]);

    static RecordedLine FeeLine(string description, decimal amount, CurrencyCode currency) =>
        new(description, new Money(amount, currency), "fees-charges", "Fees & Charges", null, EntryRole.Fee);

    static TransferView Legs(
        string fromWallet, Money from, decimal fromBalance, string toWallet, Money to, decimal toBalance,
        Money? fee = null, TransferLeg? feeLeg = null) =>
        new(Guid.Empty, fromWallet, from, Guid.Empty, toWallet, to, fee, feeLeg, StatedRate: null, VenueName: null,
            [new Money(fromBalance, from.Currency)], [new Money(toBalance, to.Currency)]);

    static CategorizationSubject TransferRecord(string raw, TransferView transfer, IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.Empty, raw, MockData.TelegramChatId, 1, transfer.FromWalletName, TransactionStatus.Completed, Day, Day, lines ?? [],
            Kind: TransactionKind.Transfer, WalletCurrency: transfer.From.Currency, WalletBalances: transfer.FromBalances,
            WalletId: transfer.FromWalletId, Transfer: transfer);
}
