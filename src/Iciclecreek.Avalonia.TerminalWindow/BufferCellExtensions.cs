using Avalonia.Media;
using System;
using XTerm.Buffer;

namespace Iciclecreek.Avalonia.Terminal
{
    public static class BufferCellExtensions
    {

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

        private static readonly Color[] _xtermFgPalette = InitializeFgPalette();
        private static readonly Color[] _xtermBgPalette = InitializeBgPalette();

        private static Color[] InitializeFgPalette()
        {
            var palette = new Color[256];
            
            palette[0] = Color.FromRgb(30, 30, 46);       // Black
            palette[1] = Color.FromRgb(241, 76, 76);      // Red
            palette[2] = Color.FromRgb(31, 186, 100);     // Green   
            palette[3] = Color.FromRgb(245, 189, 78);     // Yellow  
            palette[4] = Color.FromRgb(59, 142, 234);     // Blue    
            palette[5] = Color.FromRgb(238, 100, 238);    // Magenta 
            palette[6] = Color.FromRgb(0, 197, 202);      // Cyan    
            palette[7] = Color.FromRgb(238, 238, 238);    // White   

            palette[8] = Color.FromRgb(75, 81, 98);       // Bright Black
            palette[9] = Color.FromRgb(255, 107, 107);    // Bright Red
            palette[10] = Color.FromRgb(99, 230, 190);    // Bright Green
            palette[11] = Color.FromRgb(255, 212, 59);    // Bright Yellow
            palette[12] = Color.FromRgb(116, 192, 252);   // Bright Blue
            palette[13] = Color.FromRgb(228, 190, 254);   // Bright Magenta
            palette[14] = Color.FromRgb(150, 242, 244);   // Bright Cyan
            palette[15] = Color.FromRgb(255, 255, 255);   // Bright White

            FillRemainingPalette(palette);
            return palette;
        }

        private static Color[] InitializeBgPalette()
        {
            var palette = new Color[256];
            
            palette[0] = Color.FromRgb(15, 15, 22);
            palette[1] = Color.FromRgb(120, 30, 40);
            palette[2] = Color.FromRgb(15, 70, 35);
            palette[3] = Color.FromRgb(90, 70, 10);
            palette[4] = Color.FromRgb(15, 50, 100);
            palette[5] = Color.FromRgb(70, 25, 90);
            palette[6] = Color.FromRgb(15, 75, 80);
            palette[7] = Color.FromRgb(130, 140, 160);

            palette[8] = Color.FromRgb(45, 45, 60);
            palette[9] = Color.FromRgb(160, 40, 50);
            palette[10] = Color.FromRgb(30, 110, 50);
            palette[11] = Color.FromRgb(140, 110, 20);
            palette[12] = Color.FromRgb(30, 80, 150);
            palette[13] = Color.FromRgb(110, 45, 140);
            palette[14] = Color.FromRgb(25, 115, 120);
            palette[15] = Color.FromRgb(170, 180, 200);

            FillRemainingPalette(palette);
            return palette;
        }

        private static void FillRemainingPalette(Color[] palette)
        {
            // 16-231: 216 color cube (6x6x6)
            int index = 16;
            for (int r = 0; r < 6; r++)
            {
                for (int g = 0; g < 6; g++)
                {
                    for (int b = 0; b < 6; b++)
                    {
                        byte rv = (byte)(r > 0 ? r * 40 + 55 : 0);
                        byte gv = (byte)(g > 0 ? g * 40 + 55 : 0);
                        byte bv = (byte)(b > 0 ? b * 40 + 55 : 0);
                        palette[index++] = Color.FromRgb(rv, gv, bv);
                    }
                }
            }

            // 232-255: Grayscale ramp
            for (int i = 0; i < 24; i++)
            {
                byte gray = (byte)(8 + i * 10);
                palette[232 + i] = Color.FromRgb(gray, gray, gray);
            }
        }

        private static Color PalleteToColor(int paletteIndex, bool isBackground)
        {
            if (paletteIndex < 0 || paletteIndex >= 256)
                return Colors.White;

            return isBackground ? _xtermBgPalette[paletteIndex] : _xtermFgPalette[paletteIndex];
        }
    }
}
