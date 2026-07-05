using Avalonia.Media;
using Iciclecreek.Terminal;
using XTerm.Buffer;

namespace Iciclecreek.Avalonia.Terminal;

public static class BufferCellExtensions
{
    public static ConsolePalette ActivePalette
    {
        get;
        set
        {
            field = value;
            TerminalView.ClearAllCaches();
        }
    } = ConsolePalette.CreateDefault();

    extension(BufferCell cell)
    {
        public FontWeight GetFontWeight()
        {
            if (cell.Attributes.IsBold())
            {
                return FontWeight.Bold;
            }

            return cell.Attributes.IsDim() ? FontWeight.Thin : FontWeight.Normal;
        }

        public FontStyle GetFontStyle()
        {
            return cell.Attributes.IsItalic() ? FontStyle.Italic : FontStyle.Normal;
        }

        public TextDecorationCollection? GetTextDecorations()
        {
            if (cell.Attributes.IsUnderline())
            {
                return TextDecorations.Underline;
            }

            if (cell.Attributes.IsStrikethrough())
            {
                return TextDecorations.Strikethrough;
            }

            return cell.Attributes.IsOverline() ? TextDecorations.Overline : null;
        }

        /// <summary>
        ///     Gets the background color as RGB values.
        ///     Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public Color? GetBackgroundColor()
        {
            int color = cell.Attributes.GetBgColor();
            int mode = cell.Attributes.GetBgColorMode();

            return color == 257
                ? null
                : BufferCell.ExtractColor(color, mode, true);
        }

        public IBrush GetBackgroundBrush(IBrush defaultBrush)
        {
            Color? bgColor = cell.GetBackgroundColor();
            if (bgColor.HasValue)
            {
                return new SolidColorBrush(bgColor.Value);
            }

            return defaultBrush;
        }

        /// <summary>
        ///     Gets the foreground color as RGB values.
        ///     Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public Color? GetForegroundColor()
        {
            int color = cell.Attributes.GetFgColor();
            int mode = cell.Attributes.GetFgColorMode();
            return color == 256 
                ? null 
                : BufferCell.ExtractColor(color, mode, false);
        }

        public IBrush GetForegroundBrush(IBrush defaultBrush)
        {
            Color? fgColor = cell.GetForegroundColor();
            if (!fgColor.HasValue)
            {
                return defaultBrush;
            }

            return cell.Attributes.IsDim() 
                ? new SolidColorBrush(fgColor.Value, 0.5) 
                : new SolidColorBrush(fgColor.Value);
        }

        private static Color? ExtractColor(int color, int mode, bool isBackground)
        {
            if (mode != 1)
            {
                return PalleteToColor(color, isBackground);
            }

            int r = (color >> 16) & 0xFF;
            int g = (color >> 8) & 0xFF;
            int b = color & 0xFF;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
    }

    private static Color PalleteToColor(int paletteIndex, bool isBackground)
    {
        if (paletteIndex is < 0 or >= 256)
        {
            return Colors.White;
        }

        return isBackground
            ? ActivePalette.BgPalette[paletteIndex]
            : ActivePalette.FgPalette[paletteIndex];
    }
}