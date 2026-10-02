namespace Noof.Ledger.Domain;

public static class ReceiptKindExtensions
{
    // A copy/training/proforma/advance slip carries no receipt_lines worth re-categorizing, so its
    // correction goes down the ordinary record_transaction path instead (Fix round 1, Phase 6 final
    // review). The single definition: CategorizationWorker and ReceiptCategorizationWorker both used
    // to keep their own copy of this list, value for value.
    public static bool IsNonMoneyKind(this ReceiptKind kind) =>
        kind is ReceiptKind.Copy or ReceiptKind.Training or ReceiptKind.Proforma or ReceiptKind.Advance;

    // The one routing predicate (spec T-8, §3): only a fiscal sale or refund has amounts the receipt fixes.
    // An exchange slip is neither - its reading is corrected like spoken amounts - and stays out of
    // IsNonMoneyKind too, because it moves money between the operator's own wallets.
    public static bool IsFiscalMoneyKind(this ReceiptKind kind) => kind is ReceiptKind.Sale or ReceiptKind.Refund;
}
