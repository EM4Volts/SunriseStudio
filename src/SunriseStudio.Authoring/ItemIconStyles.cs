using SunriseStudio.Formats;

namespace SunriseStudio.Authoring;

/** How a source image becomes a native icon of one bitmap group. */
public enum ItemIconStyle
{
    /** The image is used as drawn; smaller groups are scaled copies. */
    Image,
    /** A white symbol on a clear background, like a normal perk. */
    Symbol,
    /** A white symbol on the gold disc of an exotic intrinsic perk. */
    ExoticBadge,
    /** A gold symbol on the template's own catalyst background layer. */
    CatalystPanel
}

/** Draws native icon styles from a symbol whose alpha channel is its shape. */
public static class ItemIconStyles
{
    // Exotic badge geometry and colours, in 96-pixel units, matching the stock intrinsic icons.
    private const double DiscRadius = 39.5, EdgeRadius = 41.5, RingInner = 44.5, RingOuter = 47.5;
    private const double BadgeSymbolSide = 52, SymbolSide = 86, PanelSymbolSide = 76;
    private static readonly byte[] Gold = [207, 174, 48], Edge = [219, 205, 153], PanelGold = [222, 196, 120];
    private const int Samples = 4;

    /**
     * Renders one bitmap group of an icon.
     * @param panel The catalyst background at any size; needed only by CatalystPanel.
     */
    public static RgbaImage Render(ItemIconStyle style, RgbaImage source, int width, int height,
        RgbaImage? panel = null)
    {
        source.Validate();
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (style == ItemIconStyle.Image)
            return source.Width == width && source.Height == height ? source : Scale(source, width, height);
        if (style == ItemIconStyle.CatalystPanel && panel is null)
            throw new InvalidDataException("The catalyst template has no background layer.");
        double[] mask = Mask(source);
        var (minX, minY, maxX, maxY) = Bounds(mask, source.Width, source.Height);
        double unit = Math.Min(width, height) / 96.0;
        double side = unit * (style switch
        {
            ItemIconStyle.ExoticBadge => BadgeSymbolSide,
            ItemIconStyle.CatalystPanel => PanelSymbolSide,
            _ => SymbolSide
        });
        double scale = side / Math.Max(maxX - minX, maxY - minY);
        double centerX = (minX + maxX) / 2.0, centerY = (minY + maxY) / 2.0;
        var background = style == ItemIconStyle.CatalystPanel ? Scale(panel!, width, height) : null;
        byte[] symbolColor = style == ItemIconStyle.CatalystPanel ? PanelGold : [255, 255, 255];
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double symbol = 0;
                for (int sy = 0; sy < Samples; sy++)
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        double px = x + (sx + 0.5) / Samples, py = y + (sy + 0.5) / Samples;
                        symbol += Sample(mask, source.Width, source.Height, centerX + (px - width / 2.0) / scale,
                            centerY + (py - height / 2.0) / scale);
                    }
                symbol /= Samples * Samples;
                double[] color = [0, 0, 0, 0];
                if (background is not null)
                    for (int c = 0; c < 4; c++) color[c] = background.Pixels[(y * width + x) * 4 + c] / 255.0;
                if (style == ItemIconStyle.ExoticBadge)
                    color = Over(color, Badge(x, y, width, height, unit));
                color = Over(color, [symbolColor[0] / 255.0, symbolColor[1] / 255.0, symbolColor[2] / 255.0, symbol]);
                for (int c = 0; c < 4; c++)
                    pixels[(y * width + x) * 4 + c] = (byte)Math.Clamp(Math.Round(color[c] * 255), 0, 255);
            }
        return new(width, height, pixels);
    }

    /** The disc, its light edge and the outer ring, with supersampled edges. */
    private static double[] Badge(int x, int y, int width, int height, double unit)
    {
        double disc = 0, edge = 0, ring = 0;
        for (int sy = 0; sy < Samples; sy++)
            for (int sx = 0; sx < Samples; sx++)
            {
                double dx = x + (sx + 0.5) / Samples - width / 2.0, dy = y + (sy + 0.5) / Samples - height / 2.0;
                double r = Math.Sqrt(dx * dx + dy * dy) / unit;
                if (r <= DiscRadius) disc++;
                else if (r <= EdgeRadius) edge++;
                else if (r >= RingInner && r <= RingOuter) ring++;
            }
        double n = Samples * Samples;
        double[] result = [0, 0, 0, 0];
        result = Over(result, [Gold[0] / 255.0, Gold[1] / 255.0, Gold[2] / 255.0, 0.4 * (disc + ring) / n]);
        return Over(result, [Edge[0] / 255.0, Edge[1] / 255.0, Edge[2] / 255.0, 0.78 * edge / n]);
    }

    /** Straight-alpha source-over composition. */
    private static double[] Over(double[] under, double[] over)
    {
        double alpha = over[3] + under[3] * (1 - over[3]);
        if (alpha <= 0) return [0, 0, 0, 0];
        double[] result = new double[4];
        for (int c = 0; c < 3; c++)
            result[c] = (over[c] * over[3] + under[c] * under[3] * (1 - over[3])) / alpha;
        result[3] = alpha;
        return result;
    }

    /**
     * The symbol's coverage per pixel: alpha times its main tone.
     * Detail drawn in the opposite tone, such as dark marks on a white symbol, becomes a hole.
     */
    private static double[] Mask(RgbaImage image)
    {
        int count = image.Width * image.Height;
        var luminance = new double[count];
        double weighted = 0, total = 0;
        for (int i = 0; i < count; i++)
        {
            luminance[i] = (0.2126 * image.Pixels[i * 4] + 0.7152 * image.Pixels[i * 4 + 1]
                + 0.0722 * image.Pixels[i * 4 + 2]) / 255.0;
            double alpha = image.Pixels[i * 4 + 3] / 255.0;
            weighted += alpha * luminance[i]; total += alpha;
        }
        bool light = total > 0 && weighted / total >= 0.5;
        var mask = new double[count];
        for (int i = 0; i < count; i++)
            mask[i] = image.Pixels[i * 4 + 3] / 255.0 * (light ? luminance[i] : 1 - luminance[i]);
        return mask;
    }

    /** Bilinear coverage; outside the image it is clear. */
    private static double Sample(double[] mask, int width, int height, double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0, result = 0;
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int sx = x0 + i, sy = y0 + j;
                if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                result += (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy) * mask[sy * width + sx];
            }
        return result;
    }

    /** The box around every visibly drawn pixel. */
    private static (int MinX, int MinY, int MaxX, int MaxY) Bounds(double[] mask, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (mask[y * width + x] > 0.03)
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x + 1); maxY = Math.Max(maxY, y + 1);
                }
        if (maxX < 0) throw new InvalidDataException("The symbol image is fully transparent.");
        return (minX, minY, maxX, maxY);
    }

    /** Area-averaged scaling with alpha-weighted colour. */
    public static RgbaImage Scale(RgbaImage source, int width, int height)
    {
        source.Validate();
        var pixels = new byte[width * height * 4];
        double fx = (double)source.Width / width, fy = (double)source.Height / height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double x0 = x * fx, x1 = (x + 1) * fx, y0 = y * fy, y1 = (y + 1) * fy;
                double r = 0, g = 0, b = 0, a = 0, area = 0;
                for (int sy = (int)y0; sy < Math.Min(source.Height, (int)Math.Ceiling(y1)); sy++)
                    for (int sx = (int)x0; sx < Math.Min(source.Width, (int)Math.Ceiling(x1)); sx++)
                    {
                        double w = (Math.Min(x1, sx + 1) - Math.Max(x0, sx)) * (Math.Min(y1, sy + 1) - Math.Max(y0, sy));
                        if (w <= 0) continue;
                        int at = (sy * source.Width + sx) * 4;
                        double alpha = source.Pixels[at + 3] / 255.0;
                        r += w * alpha * source.Pixels[at]; g += w * alpha * source.Pixels[at + 1];
                        b += w * alpha * source.Pixels[at + 2]; a += w * alpha; area += w;
                    }
                int to = (y * width + x) * 4;
                if (a > 0) { pixels[to] = (byte)Math.Round(r / a); pixels[to + 1] = (byte)Math.Round(g / a); pixels[to + 2] = (byte)Math.Round(b / a); }
                pixels[to + 3] = (byte)Math.Round(255 * a / area);
            }
        return new(width, height, pixels);
    }
}
