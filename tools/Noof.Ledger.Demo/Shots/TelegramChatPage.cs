using System.Net;
using System.Text;

namespace Noof.Ledger.Demo.Shots;

internal enum ChatSide
{
    Operator,
    Bot,
}

internal sealed record ChatBubble(
    ChatSide Side, string Text, string Time, bool Edited = false, IReadOnlyList<string>? Buttons = null, string? Quote = null,
    bool Photo = false);

internal sealed record ChatScene(string Name, string Title, IReadOnlyList<ChatBubble> Bubbles);

internal static class TelegramChatPage
{
    public static string Render(ChatScene scene)
    {
        var html = new StringBuilder();
        html.Append($"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>{Encode(scene.Title)}</title><style>{Css}</style></head><body>""");
        html.Append("""<header><div class="avatar">N</div><div><div class="name">Noof Ledger</div><div class="status">bot</div></div></header><main>""");
        foreach (var bubble in scene.Bubbles)
            AppendBubble(html, bubble);
        html.Append("</main></body></html>");
        return html.ToString();
    }

    static void AppendBubble(StringBuilder html, ChatBubble bubble)
    {
        html.Append($"""<div class="row {(bubble.Side == ChatSide.Operator ? "out" : "in")}"><div class="stack"><div class="bubble">""");
        if (bubble.Quote is { } quote)
            html.Append($"""<div class="quote">{Encode(quote.Split('\n')[0])}</div>""");

        if (bubble.Photo)
            html.Append(ReceiptPhoto);

        if (bubble.Text.Length > 0)
            html.Append($"""<div class="text">{Encode(bubble.Text)}</div>""");

        html.Append($"""<div class="meta">{(bubble.Edited ? "edited " : "")}{Encode(bubble.Time)}</div></div>""");
        if (bubble.Buttons is { Count: > 0 } buttons)
            html.Append($"""<div class="keyboard">{string.Concat(buttons.Select(Button))}</div>""");

        html.Append("</div></div>");
    }

    static string Button(string label) => $"""<span class="button">{Encode(label)}</span>""";

    static string Encode(string text) => WebUtility.HtmlEncode(text);

    // Drawn, not a real photo: a receipt is financial data, and none may enter the repo.
    const string ReceiptPhoto = """<div class="photo"><div class="paper"><i></i><i class="short"></i><i></i><i></i><i class="short"></i><i></i><b class="qr"></b></div></div>""";

    const string Css = """
        * { box-sizing: border-box; margin: 0; }
        body { font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; font-size: 15px; background: #c9d8b5; color: #111; min-height: 100vh; }
        header { display: flex; align-items: center; gap: 10px; padding: 10px 14px; background: #fff; border-bottom: 1px solid #d9d9d9; }
        .avatar { width: 38px; height: 38px; border-radius: 50%; background: #5caff0; color: #fff; display: flex; align-items: center; justify-content: center; font-weight: 600; }
        .name { font-weight: 600; }
        .status { font-size: 13px; color: #8a8a8a; }
        main { padding: 12px 10px 18px; display: flex; flex-direction: column; gap: 8px; }
        .row { display: flex; }
        .row.out { justify-content: flex-end; }
        .stack { max-width: 84%; display: flex; flex-direction: column; gap: 4px; }
        .bubble { padding: 6px 10px 5px; border-radius: 14px; box-shadow: 0 1px 1px rgba(0, 0, 0, .12); }
        .in .bubble { background: #fff; border-bottom-left-radius: 4px; }
        .out .bubble { background: #e1fec6; border-bottom-right-radius: 4px; }
        .text { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.35; }
        .meta { font-size: 12px; color: #8a9aa5; text-align: right; margin-top: 2px; }
        .out .meta { color: #5fa561; }
        .quote { border-left: 3px solid #3a95d5; padding: 1px 0 1px 7px; margin-bottom: 4px; color: #3a6d99; font-size: 13.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .keyboard { display: flex; gap: 4px; }
        .photo { width: 220px; height: 250px; margin: -2px -6px 4px; border-radius: 10px; background: #7d6e62; display: flex; align-items: center; justify-content: center; }
        .paper { width: 128px; height: 218px; background: #fbfaf4; transform: rotate(-4deg); padding: 14px 12px; display: flex; flex-direction: column; gap: 9px; box-shadow: 0 2px 6px rgba(0, 0, 0, .35); }
        .paper i { display: block; height: 5px; border-radius: 2px; background: #b9b5ab; }
        .paper i.short { width: 60%; }
        .qr { margin: auto auto 0; width: 56px; height: 56px; background: repeating-conic-gradient(#222 0 25%, #fbfaf4 0 50%) 0 0 / 8px 8px; outline: 4px solid #fbfaf4; }
        .button { flex: 1; text-align: center; padding: 8px 6px; border-radius: 8px; background: rgba(0, 0, 0, .22); color: #fff; font-weight: 600; font-size: 14px; }
        """;
}
