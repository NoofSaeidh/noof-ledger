namespace Noof.Ledger.Domain;

public static class ReceiptKindExtensions
{
    // A copy/training/proforma/advance slip carries no receipt_lines worth re-categorizing, so its
    // correction goes down the ordinary record_transaction path instead (Fix round 1, Phase 6 final
    // review). The single definition: CategorizationWorker and ReceiptCategorizationWorker both used
    // to keep their own copy of this list, value for value.
    public static bool IsNonMoneyKind(this ReceiptKind kind) =>
        kind is ReceiptKind.Copy or ReceiptKind.Training or ReceiptKind.Proforma or ReceiptKind.Advance;
}
