namespace Noof.Ledger.Ai;

internal static class ReceiptVisionPrompt
{
    // A short instruction sent alongside the photo in the same user turn (no separate system turn):
    // this is a one-shot answer, not a conversation, so there is nothing a system turn would say
    // that this could not say directly to the model reading the image.
    // 2026-09-27: production readings had invented whole receipts - amounts, tax ids, even shop names -
    // for photos that were not legible. This is now the one rule that matters more than completeness:
    // report only what you can actually read, leave everything else null, and say the photo is
    // unreadable rather than fill in or approximate a field. The one deliberate default kept from
    // before is the currency assumption below - explicit, and the only guess this prompt still allows:
    // Serbian fiscal receipts print RSD, so a legible receipt with no other currency shown is RSD.
    public const string Instruction =
        "Read this photograph of a shop receipt. Report only what is legible; never guess, fill in, or "
        + "approximate a field you cannot actually read. If the photo is too small, blurry, cut off, or "
        + "is not a receipt at all, set readable to false, name why, and leave every other field null or "
        + "empty - do not invent a plausible-looking receipt. When it is readable, answer with exactly "
        + "what it prints: the seller's name, its tax id (PIB) if printed, its fiscal receipt number if "
        + "printed, when it was issued, the currency (assume RSD unless another is clearly stated), the "
        + "total, how it was paid if stated, whether it is a sale or a refund, and every line item in the "
        + "order printed. Report what the receipt says; do not compute a total or correct a line that "
        + "does not add up, and never round or estimate a tax id or fiscal number you cannot read digit "
        + "by digit.";
}
