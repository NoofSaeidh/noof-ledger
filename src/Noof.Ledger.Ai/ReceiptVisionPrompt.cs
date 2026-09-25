namespace Noof.Ledger.Ai;

internal static class ReceiptVisionPrompt
{
    // A short instruction sent alongside the photo in the same user turn (no separate system turn):
    // this is a one-shot answer, not a conversation, so there is nothing a system turn would say
    // that this could not say directly to the model reading the image.
    public const string Instruction =
        "Read this photograph of a shop receipt and answer with exactly what it prints: the seller's "
        + "name, its tax id if printed, when it was issued, the currency (assume RSD unless another is "
        + "clearly stated), the total, how it was paid if stated, whether it is a sale or a refund, and "
        + "every line item in the order printed. Report what the receipt says; do not compute a total or "
        + "correct a line that does not add up.";
}
