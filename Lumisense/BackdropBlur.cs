using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Размытая копия обложки для фонов без BlurEffect (он в WPF считается на CPU): фон «Размытая обложка» окна Now Playing и вид
// «Стекло» главного окна. Все методы работают с пикселями в коде и вызываются из потока интерфейса.
internal static class BackdropBlur
{
    public static BitmapSource? Create(BitmapSource? artwork) => CreateBlurredBackdrop(artwork);

    // Размытие без BlurEffect: обложка уменьшается до 96×96, размывается по Гауссу и растягивается Catmull-Rom до 256×256.
    // Так на весь экран не видно квадратов и границ пикселей; вписывается по большей стороне, без пустых углов.
    private static BitmapSource? CreateBlurredBackdrop(BitmapSource? artwork)
    {
        if (artwork is null || artwork.PixelWidth <= 0 || artwork.PixelHeight <= 0) return null;

        const int averagedSize = 96;
        const int smoothSize = 256;
        const double blurSigma = 3.5; // в пикселях 96×96, то есть около 3,6 % ширины картинки
        double scale = Math.Max(averagedSize / (double)artwork.PixelWidth, averagedSize / (double)artwork.PixelHeight);
        double width = artwork.PixelWidth * scale;
        double height = artwork.PixelHeight * scale;

        BitmapSource averaged = RenderScaled(artwork, averagedSize,
            new Rect((averagedSize - width) / 2, (averagedSize - height) / 2, width, height));

        int stride = averagedSize * 4;
        var source = new byte[stride * averagedSize];
        averaged.CopyPixels(source, stride, 0);
        byte[] blurred = GaussianBlur(source, averagedSize, blurSigma);
        byte[] smooth = UpscaleBicubic(blurred, averagedSize, smoothSize);

        var result = BitmapSource.Create(smoothSize, smoothSize, 96, 96, PixelFormats.Pbgra32, null, smooth, smoothSize * 4);
        result.Freeze();
        return result;
    }

    // Размытие по Гауссу квадратного 4-канального изображения двумя проходами (по строкам и по столбцам); края повторяются.
    private static byte[] GaussianBlur(byte[] source, int size, double sigma)
    {
        int radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new double[radius * 2 + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = Math.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }

        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        byte[] horizontal = BlurPass(source, size, kernel, radius, horizontal: true);
        return BlurPass(horizontal, size, kernel, radius, horizontal: false);
    }

    private static byte[] BlurPass(byte[] source, int size, double[] kernel, int radius, bool horizontal)
    {
        var result = new byte[source.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int sampleX = horizontal ? Math.Clamp(x + k, 0, size - 1) : x;
                    int sampleY = horizontal ? y : Math.Clamp(y + k, 0, size - 1);
                    int index = (sampleY * size + sampleX) * 4;
                    double weight = kernel[k + radius];
                    b += source[index] * weight;
                    g += source[index + 1] * weight;
                    r += source[index + 2] * weight;
                    a += source[index + 3] * weight;
                }

                int offset = (y * size + x) * 4;
                result[offset] = ToByte(b);
                result[offset + 1] = ToByte(g);
                result[offset + 2] = ToByte(r);
                result[offset + 3] = ToByte(a);
            }
        }

        return result;
    }

    // Увеличение квадратного 4-канального изображения (Catmull-Rom). Обложка непрозрачная, поэтому предумноженная
    // прозрачность ничего не искажает.
    private static byte[] UpscaleBicubic(byte[] source, int sourceSize, int targetSize)
    {
        var target = new byte[targetSize * targetSize * 4];
        double ratio = sourceSize / (double)targetSize;
        for (int y = 0; y < targetSize; y++)
        {
            double sourceY = (y + 0.5) * ratio - 0.5;
            int baseY = (int)Math.Floor(sourceY);
            for (int x = 0; x < targetSize; x++)
            {
                double sourceX = (x + 0.5) * ratio - 0.5;
                int baseX = (int)Math.Floor(sourceX);
                double b = 0, g = 0, r = 0, a = 0, total = 0;
                for (int dy = -1; dy <= 2; dy++)
                {
                    double weightY = CatmullRom(sourceY - (baseY + dy));
                    int row = Math.Clamp(baseY + dy, 0, sourceSize - 1);
                    for (int dx = -1; dx <= 2; dx++)
                    {
                        double weight = weightY * CatmullRom(sourceX - (baseX + dx));
                        int column = Math.Clamp(baseX + dx, 0, sourceSize - 1);
                        int index = (row * sourceSize + column) * 4;
                        b += source[index] * weight;
                        g += source[index + 1] * weight;
                        r += source[index + 2] * weight;
                        a += source[index + 3] * weight;
                        total += weight;
                    }
                }

                int offset = (y * targetSize + x) * 4;
                target[offset] = ToByte(b / total);
                target[offset + 1] = ToByte(g / total);
                target[offset + 2] = ToByte(r / total);
                target[offset + 3] = ToByte(a / total);
            }
        }

        return target;
    }

    private static double CatmullRom(double distance)
    {
        double t = Math.Abs(distance);
        if (t <= 1) return 1.5 * t * t * t - 2.5 * t * t + 1;
        if (t < 2) return -0.5 * t * t * t + 2.5 * t * t - 4 * t + 2;
        return 0;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static BitmapSource RenderScaled(BitmapSource source, int size, Rect target)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(source, target);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
