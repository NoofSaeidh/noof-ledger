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
    // One family per element, never a comma-separated string: MudBlazor quotes each element as it
    // builds the CSS variable, so "a, b, c" becomes the single family name 'a, b, c', which no
    // machine has - and the whole app silently falls back to the browser's default serif. Nothing
    // errors, nothing logs. ThemeTests holds this by checking the variable the app actually serves.
    static readonly string[] SystemFonts =
        ["system-ui", "-apple-system", "Segoe UI Variable Text", "Segoe UI", "Roboto", "Helvetica Neue", "Arial"];

    // Amounts line up column-wise only in a monospaced face, and a ledger whose figures do not line
    // up is harder to read than an unstyled one. Applied as a class from app.css rather than through
    // the theme, because MudBlazor's Typography has no slot that means "numbers in a table".
    public const string AmountFont = "noof-amount";

    public static MudTheme Instance { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            // Every fill here is a light colour on a dark page, so the text drawn on it has to be dark:
            // MudBlazor's default contrast text is white, which on this green is 1.8:1 - a filled
            // "Save" or "Completed" that fails WCAG AA. ServedStylesTests measures the served pairs.
            Primary = "#7dd3a0",
            PrimaryContrastText = "#0b1f14",
            Secondary = "#8ab4f8",
            SecondaryContrastText = "#0b1526",
            Tertiary = "#c792ea",
            TertiaryContrastText = "#1d1027",
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
            // The boundary of a text field or select must reach 3:1 against what it sits on (WCAG
            // 1.4.11); the divider grey is 1.4:1, and MudBlazor's default (white at 30%) is 2.7:1.
            LinesInputs = "#6b7482",
            TableLines = "#2a313c",
            Success = "#7dd3a0",
            SuccessContrastText = "#0b1f14",
            Warning = "#f0b429",
            WarningContrastText = "#1f1600",
            Error = "#f2645a",
            ErrorContrastText = "#1f0706",
            Info = "#8ab4f8",
            InfoContrastText = "#0b1526",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = SystemFonts },
            H1 = new H1Typography { FontFamily = SystemFonts, FontWeight = "600" },
            H2 = new H2Typography { FontFamily = SystemFonts, FontWeight = "600" },
            H3 = new H3Typography { FontFamily = SystemFonts, FontWeight = "600" },
            H4 = new H4Typography { FontFamily = SystemFonts, FontWeight = "600" },
            H5 = new H5Typography { FontFamily = SystemFonts, FontWeight = "600" },
            H6 = new H6Typography { FontFamily = SystemFonts, FontWeight = "600" },
            Subtitle1 = new Subtitle1Typography { FontFamily = SystemFonts },
            Subtitle2 = new Subtitle2Typography { FontFamily = SystemFonts },
            Body1 = new Body1Typography { FontFamily = SystemFonts },
            Body2 = new Body2Typography { FontFamily = SystemFonts },
            Button = new ButtonTypography { FontFamily = SystemFonts, TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = SystemFonts },
            Overline = new OverlineTypography { FontFamily = SystemFonts },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
        },
    };
}
