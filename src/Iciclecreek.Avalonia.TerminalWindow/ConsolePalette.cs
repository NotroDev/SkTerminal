using Avalonia.Media;
using System;

namespace Iciclecreek.Avalonia.Terminal
{
    public class ConsolePalette
    {
        public Color[] FgPalette { get; set; }
        public Color[] BgPalette { get; set; }

        public ConsolePalette()
        {
            FgPalette = new Color[256];
            BgPalette = new Color[256];
        }
        
        public Color this[AnsiColor color, bool isBackground]
        {
            get => isBackground ? BgPalette[(int)color] : FgPalette[(int)color];
            set
            {
                if (isBackground)
                    BgPalette[(int)color] = value;
                else
                    FgPalette[(int)color] = value;
            }
        }
        
        public static ConsolePalette CreateDefault()
        {
            var palette = new ConsolePalette();
            palette.InitializeDefault();
            return palette;
        }

        private void InitializeDefault()
        {
            // Foreground
            this[AnsiColor.Black, false] = Color.FromRgb(30, 30, 46);
            this[AnsiColor.Red, false] = Color.FromRgb(241, 76, 76);
            this[AnsiColor.Green, false] = Color.FromRgb(31, 186, 100);
            this[AnsiColor.Yellow, false] = Color.FromRgb(245, 189, 78);
            this[AnsiColor.Blue, false] = Color.FromRgb(59, 142, 234);
            this[AnsiColor.Magenta, false] = Color.FromRgb(238, 100, 238);
            this[AnsiColor.Cyan, false] = Color.FromRgb(0, 197, 202);
            this[AnsiColor.White, false] = Color.FromRgb(238, 238, 238);

            this[AnsiColor.BrightBlack, false] = Color.FromRgb(75, 81, 98);
            this[AnsiColor.BrightRed, false] = Color.FromRgb(255, 107, 107);
            this[AnsiColor.BrightGreen, false] = Color.FromRgb(99, 230, 190);
            this[AnsiColor.BrightYellow, false] = Color.FromRgb(255, 212, 59);
            this[AnsiColor.BrightBlue, false] = Color.FromRgb(116, 192, 252);
            this[AnsiColor.BrightMagenta, false] = Color.FromRgb(228, 190, 254);
            this[AnsiColor.BrightCyan, false] = Color.FromRgb(150, 242, 244);
            this[AnsiColor.BrightWhite, false] = Color.FromRgb(255, 255, 255);

            // Background
            this[AnsiColor.Black, true] = Color.FromRgb(15, 15, 22);
            this[AnsiColor.Red, true] = Color.FromRgb(120, 30, 40);
            this[AnsiColor.Green, true] = Color.FromRgb(15, 70, 35);
            this[AnsiColor.Yellow, true] = Color.FromRgb(90, 70, 10);
            this[AnsiColor.Blue, true] = Color.FromRgb(15, 50, 100);
            this[AnsiColor.Magenta, true] = Color.FromRgb(70, 25, 90);
            this[AnsiColor.Cyan, true] = Color.FromRgb(15, 75, 80);
            this[AnsiColor.White, true] = Color.FromRgb(130, 140, 160);

            this[AnsiColor.BrightBlack, true] = Color.FromRgb(45, 45, 60);
            this[AnsiColor.BrightRed, true] = Color.FromRgb(160, 40, 50);
            this[AnsiColor.BrightGreen, true] = Color.FromRgb(30, 110, 50);
            this[AnsiColor.BrightYellow, true] = Color.FromRgb(140, 110, 20);
            this[AnsiColor.BrightBlue, true] = Color.FromRgb(30, 80, 150);
            this[AnsiColor.BrightMagenta, true] = Color.FromRgb(110, 45, 140);
            this[AnsiColor.BrightCyan, true] = Color.FromRgb(25, 115, 120);
            this[AnsiColor.BrightWhite, true] = Color.FromRgb(170, 180, 200);

            FillRemainingPalette(FgPalette);
            FillRemainingPalette(BgPalette);
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
    }
    
    public enum AnsiColor
    {
        Black = 0,
        Red = 1,
        Green = 2,
        Yellow = 3,
        Blue = 4,
        Magenta = 5,
        Cyan = 6,
        White = 7,
        BrightBlack = 8,
        BrightRed = 9,
        BrightGreen = 10,
        BrightYellow = 11,
        BrightBlue = 12,
        BrightMagenta = 13,
        BrightCyan = 14,
        BrightWhite = 15
    }
}