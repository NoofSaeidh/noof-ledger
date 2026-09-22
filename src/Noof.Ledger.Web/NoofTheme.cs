using MudBlazor;

namespace Noof.Ledger.Web;

// Dark, fixed, and defined here rather than in markup. A toggle would need interactivity plus
// somewhere to persist the choice, and MainLayout renders statically under per-page interactivity;
// a theme is only a block of CSS variables, so a fixed one costs nothing and renders either way.
//
// The font stack is the machine's own. MudBlazor's setup instructions link Roboto from
// fonts.googleapis.com, which would mean this loopback-only app making an external request on every
// page load, and not rendering properly with the network down. Neither is worth a typeface.
internal static class NoofTheme
{
    const string SystemFonts =
        "system-ui, -apple-system, 'Segoe UI Variable Text', 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif";

    // Amounts line up column-wise only in a monospaced face, and a ledger whose figures do not line
    // up is harder to read than an unstyled one. Applied as a class from app.css rather than through
    // the theme, because MudBlazor's Typography has no slot that means "numbers in a table".
    public const string AmountFont = "noof-amount";

    public static MudTheme Instance { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#7dd3a0",
            Secondary = "#8ab4f8",
            Tertiary = "#c792ea",
            Background = "#12151a",
            Surface = "#1a1f27",
            AppbarBackground = "#12151a",
            AppbarText = "#e4e7ec",
            DrawerBackground = "#12151a",
            TextPrimary = "#e4e7ec",
            TextSecondary = "#98a2b3",
            ActionDefault = "#98a2b3",
            Divider = "#2a313c",
            DividerLight = "#212832",
            LinesDefault = "#2a313c",
            TableLines = "#2a313c",
            Success = "#7dd3a0",
            Warning = "#f0b429",
            Error = "#f2645a",
            Info = "#8ab4f8",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = [SystemFonts] },
            H1 = new H1Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            H2 = new H2Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            H3 = new H3Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            H4 = new H4Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            H5 = new H5Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            H6 = new H6Typography { FontFamily = [SystemFonts], FontWeight = "600" },
            Subtitle1 = new Subtitle1Typography { FontFamily = [SystemFonts] },
            Subtitle2 = new Subtitle2Typography { FontFamily = [SystemFonts] },
            Body1 = new Body1Typography { FontFamily = [SystemFonts] },
            Body2 = new Body2Typography { FontFamily = [SystemFonts] },
            Button = new ButtonTypography { FontFamily = [SystemFonts], TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = [SystemFonts] },
            Overline = new OverlineTypography { FontFamily = [SystemFonts] },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
        },
    };
}
