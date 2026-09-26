using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class TelegramChatPageTests
{
    [Fact]
    public void A_bot_reply_shows_its_text_its_edited_mark_and_its_buttons()
    {
        var html = TelegramChatPage.Render(new ChatScene("expense", "Expense",
        [
            new ChatBubble(ChatSide.Operator, "coffee 350 rsd", "18:30"),
            new ChatBubble(ChatSide.Bot, "Recorded — Raiffeisen\n• Coffee", "18:30", Edited: true, Buttons: ["Cancel", "Edit"]),
        ]));

        html.Should().Contain("coffee 350 rsd");
        html.Should().Contain("Recorded — Raiffeisen\n• Coffee");
        html.Should().Contain("edited 18:30");
        html.Should().Contain(">Cancel<").And.Contain(">Edit<");
        html.Should().Contain("class=\"row out\"").And.Contain("class=\"row in\"");
    }

    [Fact]
    public void Text_is_html_encoded()
    {
        var html = TelegramChatPage.Render(new ChatScene("x", "X", [new ChatBubble(ChatSide.Operator, "<b>#@%&</b>", "09:00")]));

        html.Should().Contain("&lt;b&gt;#@%&amp;&lt;/b&gt;");
        html.Should().NotContain("<b>#@%&</b>");
    }

    [Fact]
    public void A_reply_quotes_only_the_first_line_of_what_it_answers()
    {
        var html = TelegramChatPage.Render(new ChatScene("x", "X",
            [new ChatBubble(ChatSide.Operator, "no, 42", "09:00", Quote: "What should I fix?\nsecond line")]));

        html.Should().Contain("<div class=\"quote\">What should I fix?</div>");
        html.Should().NotContain("second line");
    }
}
