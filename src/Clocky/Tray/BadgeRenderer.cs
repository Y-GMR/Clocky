using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Clocky.Tray;

public static class BadgeRenderer
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon RenderBadge(string text, Color backgroundColor, Color textColor, Color? borderColor = null, int size = 32)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            // Draw rounded badge rectangle with clean 1px inset for crisp borders
            int radius = size >= 32 ? 6 : (size <= 16 ? 3 : 5);
            using var path = CreateRoundedRectanglePath(new Rectangle(1, 1, size - 2, size - 2), radius);
            using var brush = new SolidBrush(backgroundColor);
            g.FillPath(brush, path);

            // Adaptive High-Contrast Border (crucial for white/light badges or dark taskbars)
            Color borderCol = borderColor ?? (IsLightColor(backgroundColor) ? Color.FromArgb(180, 40, 50, 65) : Color.FromArgb(70, 255, 255, 255));
            using var borderPen = new Pen(borderCol, size >= 32 ? 1.4f : 1f);
            g.DrawPath(borderPen, path);

            // Maximized, bold typography filling badge area
            float fontSize;
            if (text.Length <= 1)
                fontSize = size >= 32 ? 22f : 10f;
            else if (text.Length == 2)
                fontSize = size >= 32 ? 19.5f : 9f;
            else if (text.Length == 3)
                fontSize = size >= 32 ? 14f : 6.5f;
            else
                fontSize = size >= 32 ? 11f : 5.5f;

            Font font;
            try
            {
                font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            using (font)
            using (var textBrush = new SolidBrush(textColor))
            {
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                };

                // Exact vertical centroid balance
                var textRect = new RectangleF(0, size >= 32 ? -0.5f : 0f, size, size);
                g.DrawString(text, font, textBrush, textRect, format);
            }
        }

        IntPtr hIcon = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static bool IsLightColor(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum > 0.65;
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        // Top-left
        path.AddArc(arc, 180, 90);
        // Top-right
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        // Bottom-right
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        // Bottom-left
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
