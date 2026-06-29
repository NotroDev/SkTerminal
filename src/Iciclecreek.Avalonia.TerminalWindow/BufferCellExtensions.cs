using Avalonia.Media;
using System;
using XTerm.Buffer;

namespace Iciclecreek.Avalonia.Terminal
{
    public static class BufferCellExtensions
    {
        public static ConsolePalette ActivePalette { get; set; } = ConsolePalette.CreateDefault();

        public static FontWeight GetFontWeight(this BufferCell cell)
        {
            if (cell.Attributes.IsBold())
                return FontWeight.Bold;
            if (cell.Attributes.IsDim())
                return FontWeight.Thin;
            return FontWeight.Normal;
        }

        public static FontStyle GetFontStyle(this BufferCell cell)
        {
            if (cell.Attributes.IsItalic())
                return FontStyle.Italic;
            return FontStyle.Normal;
        }

        public static TextDecorationCollection? GetTextDecorations(this BufferCell cell)
        {
            if (cell.Attributes.IsUnderline())
                return TextDecorations.Underline;
            if (cell.Attributes.IsStrikethrough())
                return TextDecorations.Strikethrough;
            if (cell.Attributes.IsOverline())
                return TextDecorations.Overline;
            return null;
        }

        /// <summary>
        /// Gets the background color as RGB values.
        /// Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public static Color? GetBackgroundColor(this BufferCell cell)
        {
            var color = cell.Attributes.GetBgColor();
            var mode = cell.Attributes.GetBgColorMode();

            if (color == 257) return null;  // Default color

            return cell.ExtractColor(color, mode, isBackground: true);
        }

        public static IBrush GetBackgroundBrush(this BufferCell cell, IBrush defaultBrush)
        {
            var bgColor = cell.GetBackgroundColor();
            if (bgColor.HasValue)
            {
                return new SolidColorBrush(bgColor.Value);
            }
            return defaultBrush;
        }

        /// <summary>
        /// Gets the foreground color as RGB values.
        /// Returns null if using default color or palette mode.
        /// </summary>
        /// <returns>A tuple (R, G, B) with RGB values 0-255, or null if not using RGB mode.</returns>
        public static Color? GetForegroundColor(this BufferCell cell)
        {
            var color = cell.Attributes.GetFgColor();
            var mode = cell.Attributes.GetFgColorMode();
            if (color == 256)
                return null;

            return cell.ExtractColor(color, mode, isBackground: false);
        }

        public static IBrush GetForegroundBrush(this BufferCell cell, IBrush defaultBrush)
        {
            var fgColor = cell.GetForegroundColor();
            if (fgColor.HasValue)
            {
                if (cell.Attributes.IsDim())
                    return new SolidColorBrush(fgColor.Value, 0.5);
            
                return new SolidColorBrush(fgColor.Value);
            }
            return defaultBrush;
        }

        private static Color? ExtractColor(this BufferCell cell, int color, int mode, bool isBackground)
        {
            if (mode == 1)
            {
                int r = (color >> 16) & 0xFF;
                int g = (color >> 8) & 0xFF;
                int b = color & 0xFF;
                return Color.FromRgb((byte)r, (byte)g, (byte)b);
            }
            
            return PalleteToColor(color, isBackground);
        }
        
        private static Color PalleteToColor(int paletteIndex, bool isBackground)
        {
            if (paletteIndex < 0 || paletteIndex >= 256)
                return Colors.White;

            return isBackground 
                ? ActivePalette.BgPalette[paletteIndex] 
                : ActivePalette.FgPalette[paletteIndex];
        }
    }
}
