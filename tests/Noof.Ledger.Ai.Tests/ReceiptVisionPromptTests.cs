using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests;

public class ReceiptVisionPromptTests
{
    [Fact]
    public void The_instruction_reads_a_shop_receipt_or_an_exchange_office_slip_and_never_calls_a_slip_not_a_receipt()
    {
        ReceiptVisionPrompt.Instruction.Should().NotContain("photograph of a shop receipt");
        ReceiptVisionPrompt.Instruction.Should().Contain("an exchange slip is never \"not a receipt\"");
        ReceiptVisionPrompt.Instruction.Should().Contain("is neither a shop receipt nor an");
        ReceiptVisionPrompt.Instruction.Should().Contain("set kind to exchange");
        ReceiptVisionPrompt.Instruction.Should().Contain("Leave every exchange field null.");
    }

    [Fact]
    public void The_instruction_keeps_the_settled_never_invent_rule()
    {
        ReceiptVisionPrompt.Instruction.Should().Contain("never guess, fill in, or approximate a field you cannot actually");
        ReceiptVisionPrompt.Instruction.Should().Contain("do not invent a plausible-looking document");
        ReceiptVisionPrompt.Instruction.Should().Contain("Any exchange figure you cannot read is null.");
    }

    [Fact]
    public void The_instruction_lists_the_fields_the_NBS_requires_on_a_slip()
    {
        ReceiptVisionPrompt.Instruction.Should().Contain("the slip's serial number;");
        ReceiptVisionPrompt.Instruction.Should().Contain("the amount in dinars; the rate applied;");
        ReceiptVisionPrompt.Instruction.Should().Contain("the commission's percentage and amount");
        ReceiptVisionPrompt.Instruction.Should().Contain("never in fiscal_number");
    }

    [Fact]
    public void The_instruction_turns_the_offices_buying_and_selling_into_what_the_customer_gave_and_received()
    {
        ReceiptVisionPrompt.Instruction.Should().Contain(
            "A slip speaks from the office's side; the exchange fields are the customer's.");
        ReceiptVisionPrompt.Instruction.Should().Contain("foreign currency and received dinars");
        ReceiptVisionPrompt.Instruction.Should().Contain("gave dinars and received the foreign currency");
    }

    [Fact]
    public void The_instruction_never_asks_the_model_to_work_a_figure_out()
    {
        ReceiptVisionPrompt.Instruction.Should().Contain("Report each amount exactly as printed; never compute one.");
        ReceiptVisionPrompt.Instruction.Should().Contain("use the amount paid out or in");
        ReceiptVisionPrompt.Instruction.Should().Contain("rate is the rate exactly as printed, never converted or recomputed.");
        ReceiptVisionPrompt.Instruction.Should().NotContain("changed hands once");
        ReceiptVisionPrompt.Instruction.Should().NotContain("per one unit",
            "asking for a per-unit rate would make the model divide a rate a slip quotes per 100 units");
    }
}
