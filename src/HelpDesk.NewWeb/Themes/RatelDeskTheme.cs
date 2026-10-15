using MudBlazor;

namespace HelpDesk.NewWeb.Themes;

public sealed class RatelDeskTheme : MudTheme
{
    public RatelDeskTheme()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#b84e2e",
            PrimaryDarken = "#933b22",
            PrimaryLighten = "#d87552",
            PrimaryContrastText = "#ffffff",
            Secondary = "#176f6a",
            Tertiary = "#315e9b",
            AppbarBackground = "#ffffff",
            AppbarText = "#152233",
            Background = "#f5f7fa",
            BackgroundGray = "#eef1f5",
            Surface = "#ffffff",
            DrawerBackground = "#ffffff",
            DrawerText = "#263545",
            DrawerIcon = "#627283",
            TextPrimary = "#152233",
            TextSecondary = "#5a6979",
            TextDisabled = "#95a1ae",
            ActionDefault = "#526273",
            ActionDisabled = "#a4afbb",
            ActionDisabledBackground = "#e8edf2",
            Info = "#3778c2",
            Success = "#248066",
            Warning = "#a76513",
            Error = "#bf3f45",
            LinesDefault = "rgba(21, 34, 51, 0.14)",
            LinesInputs = "rgba(21, 34, 51, 0.22)",
            TableLines = "rgba(21, 34, 51, 0.11)",
            Divider = "rgba(21, 34, 51, 0.14)",
            DividerLight = "rgba(21, 34, 51, 0.08)",
            OverlayDark = "rgba(15, 23, 34, 0.58)",
            Black = "#152233",
            White = "#ffffff",
        };

        PaletteDark = new PaletteDark
        {
            Primary = "#d27655",
            PrimaryDarken = "#ad593d",
            PrimaryLighten = "#e79a7b",
            PrimaryContrastText = "#21110c",
            Secondary = "#55aaa2",
            Tertiary = "#81aee4",
            AppbarBackground = "#15191f",
            AppbarText = "#edf1f5",
            Background = "#0c0f13",
            BackgroundGray = "#11161c",
            Surface = "#171c23",
            DrawerBackground = "#12171d",
            DrawerText = "#dce3ea",
            DrawerIcon = "#9eacba",
            TextPrimary = "#edf1f5",
            TextSecondary = "#aeb9c5",
            TextDisabled = "#6d7986",
            ActionDefault = "#aeb9c5",
            ActionDisabled = "rgba(174, 185, 197, 0.38)",
            ActionDisabledBackground = "rgba(174, 185, 197, 0.15)",
            Info = "#83afe6",
            Success = "#57b890",
            Warning = "#d6a34d",
            Error = "#de777d",
            LinesDefault = "rgba(237, 241, 245, 0.14)",
            LinesInputs = "rgba(237, 241, 245, 0.20)",
            TableLines = "rgba(237, 241, 245, 0.11)",
            TableStriped = "rgba(237, 241, 245, 0.025)",
            Divider = "rgba(237, 241, 245, 0.14)",
            DividerLight = "rgba(237, 241, 245, 0.08)",
            OverlayDark = "rgba(0, 0, 0, 0.7)",
            OverlayLight = "rgba(21, 25, 31, 0.76)",
            Black = "#080a0d",
            White = "#ffffff",
        };

        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "7px",
            DrawerWidthLeft = "272px",
            AppbarHeight = "56px",
        };

        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Roboto", "Helvetica Neue", "Arial", "sans-serif" },
                FontWeight = "400",
                FontSize = "1rem",
                LineHeight = "1.5",
                LetterSpacing = "0",
            },
            H1 = new H1Typography { FontSize = "2.25rem", FontWeight = "700", LetterSpacing = "0" },
            H2 = new H2Typography { FontSize = "1.875rem", FontWeight = "700", LetterSpacing = "0" },
            H3 = new H3Typography { FontSize = "1.5rem", FontWeight = "650", LetterSpacing = "0" },
            H4 = new H4Typography { FontSize = "1.3rem", FontWeight = "650", LetterSpacing = "0" },
            H5 = new H5Typography { FontSize = "1.12rem", FontWeight = "650", LetterSpacing = "0" },
            H6 = new H6Typography { FontSize = "1rem", FontWeight = "600", LetterSpacing = "0" },
            Button = new ButtonTypography { FontWeight = "600", TextTransform = "none", LetterSpacing = "0" },
        };

        Shadows = new Shadow();
        ZIndex = new ZIndex();
    }
}
