using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace SamaDisplay.Core.Rendering;

public sealed class DashboardRenderer
{
    public const int Size = 480;

    private sealed class Palette
    {
        public Color Bg1, Bg2, Card, Card2, Border, Text, Muted, Accent, Accent2, Good, Warn, Bad;
    }

    private static Palette GetPalette(string theme)
    {
        return (theme ?? "Dark").ToLowerInvariant() switch
        {
            "neon" => new Palette
            {
                Bg1 = Color.FromArgb(4, 6, 12), Bg2 = Color.FromArgb(10, 4, 24),
                Card = Color.FromArgb(14, 18, 28), Card2 = Color.FromArgb(20, 26, 40),
                Border = Color.FromArgb(34, 44, 62), Text = Color.FromArgb(245, 250, 255),
                Muted = Color.FromArgb(130, 150, 180), Accent = Color.FromArgb(0, 229, 255),
                Accent2 = Color.FromArgb(255, 61, 129), Good = Color.FromArgb(57, 255, 176),
                Warn = Color.FromArgb(255, 196, 0), Bad = Color.FromArgb(255, 82, 82)
            },
            "minimal" or "light" => new Palette
            {
                Bg1 = Color.FromArgb(243, 246, 251), Bg2 = Color.FromArgb(226, 232, 240),
                Card = Color.White, Card2 = Color.FromArgb(248, 250, 253),
                Border = Color.FromArgb(226, 232, 240), Text = Color.FromArgb(17, 24, 39),
                Muted = Color.FromArgb(107, 114, 128), Accent = Color.FromArgb(37, 99, 235),
                Accent2 = Color.FromArgb(14, 165, 233), Good = Color.FromArgb(16, 185, 129),
                Warn = Color.FromArgb(217, 119, 6), Bad = Color.FromArgb(220, 38, 38)
            },
            _ => new Palette
            {
                Bg1 = Color.FromArgb(7, 10, 16), Bg2 = Color.FromArgb(16, 24, 39),
                Card = Color.FromArgb(20, 28, 42), Card2 = Color.FromArgb(26, 36, 52),
                Border = Color.FromArgb(38, 50, 70), Text = Color.FromArgb(248, 250, 252),
                Muted = Color.FromArgb(143, 160, 184), Accent = Color.FromArgb(124, 92, 255),
                Accent2 = Color.FromArgb(34, 211, 238), Good = Color.FromArgb(52, 211, 153),
                Warn = Color.FromArgb(251, 191, 36), Bad = Color.FromArgb(251, 113, 133)
            }
        };
    }

    public Bitmap RenderBitmap(SensorSnapshot s, AppSettings settings)
    {
        var p = GetPalette(settings.Theme);
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        g.Clear(p.Bg1);

        DrawHeader(g, p, s, settings);
        DrawCpuCard(g, p, s, settings, new RectangleF(22, 76, 436, 112));
        DrawGpuCard(g, p, s, settings, new RectangleF(22, 198, 436, 112));
        DrawRamCard(g, p, s, settings, new RectangleF(22, 320, 436, 112));
        DrawFooter(g, p, s, settings);
        return bmp;
    }

    public byte[] RenderJpeg(SensorSnapshot s, AppSettings settings)
    {
        using var bmp = RenderBitmap(s, settings);
        using var ms = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var pars = new EncoderParameters(1);
        pars.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(settings.JpegQuality, 40, 95));
        bmp.Save(ms, encoder, pars);
        var bytes = ms.ToArray();
        Logger.Info($"render jpeg {bytes.Length} bytes (q={settings.JpegQuality})");
        return bytes;
    }

    public byte[] RenderBgra(SensorSnapshot s, AppSettings settings)
    {
        using var bmp = RenderBitmap(s, settings);
        // The panel stores the frame rotated 90 degrees clockwise.
        bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
        var rect = new Rectangle(0, 0, Size, Size);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[Size * Size * 4];
        try
        {
            for (var y = 0; y < Size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * Size * 4, Size * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bytes;
    }

    private static void DrawHeader(Graphics g, Palette p, SensorSnapshot s, AppSettings cfg)
    {
        using var logoFont = new Font("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var timeFont = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        using var dateFont = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(p.Text);
        using var mutedBrush = new SolidBrush(p.Muted);
        using var accentBrush = new SolidBrush(p.Accent);

        g.DrawString("SAMA", logoFont, textBrush, 20, 8);
        var w = g.MeasureString("SAMA", logoFont).Width;
        g.DrawString("DISPLAY", subFont, accentBrush, 20 + w + 4, 24);

        var time = s.Timestamp.ToString("HH:mm:ss");
        var tw = g.MeasureString(time, timeFont).Width;
        g.DrawString(time, timeFont, textBrush, Size - 22 - tw, 2);
        var date = s.Timestamp.ToString("yyyy-MM-dd  dddd");
        var dw = g.MeasureString(date, dateFont).Width;
        g.DrawString(date, dateFont, mutedBrush, Size - 22 - dw, 45);
    }

    private static void DrawCpuCard(Graphics g, Palette p, SensorSnapshot s, AppSettings cfg, RectangleF r)
    {
        DrawCard(g, p, r, p.Accent);
        using var labelFont = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bigFont = new Font("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);

        using var accent = new SolidBrush(p.Accent);
        using var text = new SolidBrush(p.Text);
        using var muted = new SolidBrush(p.Muted);

        g.DrawString("CPU", labelFont, accent, r.X + 16, r.Y + 10);
        g.DrawString(Trim(g, s.CpuName, nameFont, 245), nameFont, muted, r.X + 16, r.Y + 26);

        var tempText = FormatTemp(s.CpuTemp, cfg);
        var tw = g.MeasureString(tempText, bigFont).Width;
        g.DrawString(tempText, bigFont, text, r.Right - 18 - tw, r.Y + 4);
        g.DrawString(cfg.Fahrenheit ? "°F" : "°C", unitFont, muted, r.Right - 18, r.Y + 22);

        var load = s.CpuLoad ?? 0;
        DrawBar(g, p, new RectangleF(r.X + 16, r.Y + 56, r.Width - 32, 8), load / 100.0, p.Accent);

        var y = r.Y + 74;
        DrawMetric(g, "LOAD", $"{load:0}%", r.X + 16, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "CLOCK", s.CpuClockMhz is > 0 ? $"{s.CpuClockMhz / 1000.0:0.0} GHz" : "--", r.X + 125, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "POWER", s.CpuPowerW is > 0 ? $"{s.CpuPowerW:0} W" : "--", r.X + 250, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "FAN", s.CpuFanRpm is > 0 ? $"{s.CpuFanRpm:0} RPM" : "--", r.X + 355, y, valueFont, smallFont, text, muted);
    }

    private static void DrawGpuCard(Graphics g, Palette p, SensorSnapshot s, AppSettings cfg, RectangleF r)
    {
        DrawCard(g, p, r, p.Accent2);
        using var labelFont = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bigFont = new Font("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);

        using var accent = new SolidBrush(p.Accent2);
        using var text = new SolidBrush(p.Text);
        using var muted = new SolidBrush(p.Muted);

        g.DrawString("GPU", labelFont, accent, r.X + 16, r.Y + 10);
        g.DrawString(Trim(g, s.GpuName, nameFont, 245), nameFont, muted, r.X + 16, r.Y + 26);

        var tempText = FormatTemp(s.GpuTemp, cfg);
        var tw = g.MeasureString(tempText, bigFont).Width;
        g.DrawString(tempText, bigFont, text, r.Right - 18 - tw, r.Y + 4);
        g.DrawString(cfg.Fahrenheit ? "°F" : "°C", unitFont, muted, r.Right - 18, r.Y + 22);

        var load = s.GpuLoad ?? 0;
        DrawBar(g, p, new RectangleF(r.X + 16, r.Y + 56, r.Width - 32, 8), load / 100.0, p.Accent2);

        var y = r.Y + 74;
        DrawMetric(g, "LOAD", $"{load:0}%", r.X + 16, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "CLOCK", s.GpuClockMhz is > 0 ? $"{s.GpuClockMhz:0} MHz" : "--", r.X + 125, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "FAN", s.GpuFanRpm is > 0 ? $"{s.GpuFanRpm:0} RPM" : "--", r.X + 250, y, valueFont, smallFont, text, muted);
        var vram = s.GpuMemUsedMb is > 0
            ? $"{s.GpuMemUsedMb / 1024.0:0.0}/{Math.Max(s.GpuMemTotalMb ?? 0, s.GpuMemUsedMb ?? 0) / 1024.0:0.0}G"
            : "--";
        DrawMetric(g, "VRAM", vram, r.X + 355, y, valueFont, smallFont, text, muted);
    }

    private static void DrawRamCard(Graphics g, Palette p, SensorSnapshot s, AppSettings cfg, RectangleF r)
    {
        DrawCard(g, p, r, p.Good);
        using var labelFont = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bigFont = new Font("Segoe UI", 27, FontStyle.Bold, GraphicsUnit.Pixel);
        using var unitFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);

        using var accent = new SolidBrush(p.Good);
        using var text = new SolidBrush(p.Text);
        using var muted = new SolidBrush(p.Muted);

        g.DrawString("MEMORY", labelFont, accent, r.X + 16, r.Y + 10);
        g.DrawString(Trim(g, s.HostName + "  ·  " + (s.Uptime.ToString(@"dd\.hh\:mm")), nameFont, 245), nameFont, muted, r.X + 16, r.Y + 26);

        var pct = s.RamPercent ?? 0;
        var big = $"{pct:0}%";
        var tw = g.MeasureString(big, bigFont).Width;
        g.DrawString(big, bigFont, text, r.Right - 18 - tw, r.Y + 4);

        DrawBar(g, p, new RectangleF(r.X + 16, r.Y + 56, r.Width - 32, 8), pct / 100.0, p.Good);

        var y = r.Y + 74;
        var used = s.RamUsedGb ?? 0;
        var total = s.RamTotalGb ?? 0;
        DrawMetric(g, "USED", total > 0 ? $"{used:0.0} GB" : "--", r.X + 16, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "TOTAL", total > 0 ? $"{total:0.0} GB" : "--", r.X + 160, y, valueFont, smallFont, text, muted);
        DrawMetric(g, "FREE", total > 0 ? $"{Math.Max(0, total - used):0.0} GB" : "--", r.X + 304, y, valueFont, smallFont, text, muted);
    }

    private static void DrawFooter(Graphics g, Palette p, SensorSnapshot s, AppSettings cfg)
    {
        using var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
        using var muted = new SolidBrush(p.Muted);
        using var accent = new SolidBrush(p.Accent);
        var left = s.HardwareAvailable ? "HWiNFO/LHM · live" : "fallback sensors";
        g.DrawString(left, font, muted, 22, 446);
        var right = $"SAMA Display · {cfg.IntervalSeconds}s";
        var rw = g.MeasureString(right, font).Width;
        g.DrawString(right, font, accent, Size - 22 - rw, 446);
    }

    private static void DrawCard(Graphics g, Palette p, RectangleF r, Color accent)
    {
        using var path = Rounded(r, 14);
        using var fill = new SolidBrush(p.Card);
        g.FillPath(fill, path);
        using var pen = new Pen(p.Border, 1);
        g.DrawPath(pen, path);
        using var strip = Rounded(new RectangleF(r.X + 1, r.Y + 18, 4, r.Height - 36), 2);
        using var brush = new SolidBrush(accent);
        g.FillPath(brush, strip);
    }

    private static void DrawBar(Graphics g, Palette p, RectangleF r, double fraction, Color color)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        using (var bg = Rounded(r, r.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb(p.Text.A > 0 ? 26 : 26, p.Text)))
            g.FillPath(b, bg);
        if (fraction <= 0) return;
        var fillRect = new RectangleF(r.X, r.Y, (float)Math.Max(r.Height, r.Width * fraction), r.Height);
        using var fillPath = Rounded(fillRect, r.Height / 2);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, fillPath);
    }

    private static void DrawMetric(Graphics g, string label, string value, float x, float y,
        Font valueFont, Font labelFont, Brush text, Brush muted)
    {
        g.DrawString(label, labelFont, muted, x, y + 13);
        g.DrawString(value, valueFont, text, x, y - 4);
    }

    private static string FormatTemp(double? c, AppSettings cfg)
    {
        if (c is not > 0) return "--";
        var v = cfg.Fahrenheit ? c.Value * 9 / 5 + 32 : c.Value;
        return $"{v:0}";
    }

    private static string Trim(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        while (text.Length > 3 && g.MeasureString(text + "…", font).Width > maxWidth)
            text = text[..^1];
        return text + "…";
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Lighten(Color c, float amount)
    {
        return Color.FromArgb(c.A,
            (int)Math.Min(255, c.R + (255 - c.R) * amount),
            (int)Math.Min(255, c.G + (255 - c.G) * amount),
            (int)Math.Min(255, c.B + (255 - c.B) * amount));
    }
}
