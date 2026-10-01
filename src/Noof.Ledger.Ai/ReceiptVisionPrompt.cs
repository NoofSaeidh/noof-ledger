namespace Noof.Ledger.Ai;

internal static class ReceiptVisionPrompt
{
    // A short instruction sent alongside the photo in the same user turn (no separate system turn):
    // this is a one-shot answer, not a conversation, so there is nothing a system turn would say
    // that this could not say directly to the model reading the image.
    // 2026-09-27: production readings had invented whole receipts - amounts, tax ids, even shop names -
    // for photos that were not legible. This is now the one rule that matters more than completeness:
    // report only what you can actually read, leave everything else null, and say the photo is
    // unreadable rather than fill in or approximate a field. This prompt asks for no default at all -
    // the one deliberate default kept from before (RSD when no currency is printed) lives in C#, in
    // ChatReceiptVision's own mapping from a null currency, since Serbian fiscal receipts print RSD
    // and a legible receipt showing no other currency is one; docs/decisions/p6-2-vision-fallback-stopped-inventing-receipts.md.
    public const string Instruction = """
        Read this photograph. It is a shop receipt or an exchange-office slip - a menjačnica's
        confirmation that it bought (otkup) or sold (prodaja) foreign cash. Both are documents you read
        here: an exchange slip is never "not a receipt".

        Report only what is legible; never guess, fill in, or approximate a field you cannot actually
        read. If the photo is too small, blurry, cut off, or is neither a shop receipt nor an
        exchange-office slip, set readable to false, name why, and leave every other field null or
        empty - do not invent a plausible-looking document. Report what the document says; do not
        compute a figure it does not print or correct one that does not add up, and never round or
        estimate a tax id, fiscal number or slip number you cannot read digit by digit.

        A shop receipt: answer with exactly what it prints - the seller's name, its tax id (PIB) if
        printed, its fiscal receipt number if printed, when it was issued, the currency if one is
        printed (leave it null if you cannot tell), the total, how it was paid if stated, whether it
        is a sale or a refund (or null if that is not legible - never guess between them), and every
        line item in the order printed. Leave every exchange field null.

        An exchange-office slip: set kind to exchange. The National Bank of Serbia's rules for exchange
        offices (item 23) require a slip to print the office's name; the name and address of its
        exchange point; the cash desk's code; whether the office bought (otkup) or sold (prodaja)
        foreign cash; the slip's serial number; the currency's code, the amount in that currency and
        the amount in dinars; the rate applied; the commission's percentage and amount when one was
        charged; and the date, time and place. Put the office's name in seller_name, its PIB in
        seller_tax_id if printed, and the date and time in issued_at. Put the slip's serial number,
        exactly as printed, in exchange.slip_number - never in fiscal_number, which stays null on a
        slip, as do currency and total; lines is empty.

        A slip speaks from the office's side; the exchange fields are the customer's. When the office
        bought foreign cash (otkup, also printed as kupovina or kupovni kurs), the customer gave the
        foreign currency and received dinars. When it sold (prodaja, prodajni kurs), the customer
        gave dinars and received the foreign currency. given_amount and given_currency are what the
        customer handed over, received_amount and received_currency what the customer was handed.
        Report each amount exactly as printed; never compute one. If the slip prints both the
        counter-value and the amount paid out or in, use the amount paid out or in, and put a printed
        commission in commission. rate is the rate exactly as printed, never converted or recomputed.
        Currencies are three-letter codes (RSD for dinars). Any exchange figure you cannot read is null.
        """;
}
