using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lumisense;

// Фон «Облака»: вся поверхность переливается цветами обложки. До восьми цветов (кластеры обложки с весами по площади) задают
// «источники»: каждый медленно плывёт по своей замкнутой траектории, а цвет точки поля — взвешенная смесь цветов всех источников
// (вес убывает с расстоянием как 1 / (1 + d²/σ²)³), поэтому нет ни сфер, ни границ, а цвета перетекают друг в друга.
// Поле считается в маленький битмап (как у «Волн»), который растягивается на окно с линейным сглаживанием: никакого BlurEffect.
public partial class NowPlayingWindow
{
    private const int CloudPointCount = 8;
    private const int CloudBitmapWidth = 96;
    private const double CloudColorBlendPerSecond = 3.5;
    private const float CloudBaseWeight = 0.01f;
    private const float CloudSaturation = 1.35f;

    private sealed class CloudPoint
    {
        public float R, G, B, Mass;
        public float TargetR, TargetG, TargetB, TargetMass;
        public double CenterX, CenterY, AmplitudeX, AmplitudeY, FrequencyX, FrequencyY, PhaseX, PhaseY, Sigma;
    }

    private readonly CloudPoint[] _cloudPoints = CreateCloudPoints();
    private WriteableBitmap? _cloudBitmap;
    private byte[] _cloudPixels = Array.Empty<byte>();
    private int _cloudHeight;
    private float _cloudBaseR = 20, _cloudBaseG = 22, _cloudBaseB = 30;
    private bool _cloudHasPalette;

    // Точки раскиданы равномерно (последовательность с малым расхождением), частоты и фазы разные: движение не повторяется быстро.
    private static CloudPoint[] CreateCloudPoints()
    {
        static double Fraction(double value) => value - Math.Floor(value);

        var points = new CloudPoint[CloudPointCount];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = new CloudPoint
            {
                CenterX = 0.12 + 0.76 * Fraction(0.5 + index * 0.7548776662),
                CenterY = 0.12 + 0.76 * Fraction(0.5 + index * 0.5698402910),
                AmplitudeX = 0.20 + 0.12 * Fraction(index * 0.618034),
                AmplitudeY = 0.18 + 0.12 * Fraction(index * 0.414214 + 0.3),
                FrequencyX = 0.55 + 0.70 * Fraction(index * 0.381966 + 0.13),
                FrequencyY = 0.50 + 0.75 * Fraction(index * 0.236068 + 0.41),
                PhaseX = index * 2.399963,
                PhaseY = index * 1.618034 + 0.7,
                Sigma = 0.30 + 0.14 * Fraction(index * 0.137 + 0.2)
            };
        }

        return points;
    }

    // Новые целевые цвета: при работающей анимации точки плавно перетекают к ним, иначе (первое окно, «Снизить движение») сразу.
    private void SetCloudPalette(PalettePoint[] clusters, bool instant)
    {
        if (clusters.Length == 0) return;

        double maxWeight = clusters.Max(cluster => cluster.Weight);
        double totalWeight = clusters.Sum(cluster => cluster.Weight);
        if (maxWeight <= 0 || totalWeight <= 0) return;

        for (int index = 0; index < _cloudPoints.Length; index++)
        {
            PalettePoint cluster = clusters[index % clusters.Length];

            // Если цветов меньше, чем точек, лишние точки повторяют цвета, но с меньшей массой.
            double share = cluster.Weight / maxWeight * (index < clusters.Length ? 1.0 : 0.55);
            CloudPoint point = _cloudPoints[index];
            point.TargetR = (float)Math.Min(255, cluster.Red * 1.06);
            point.TargetG = (float)Math.Min(255, cluster.Green * 1.06);
            point.TargetB = (float)Math.Min(255, cluster.Blue * 1.06);
            point.TargetMass = (float)(0.40 + 0.60 * Math.Sqrt(Math.Clamp(share, 0, 1)));

            if (instant || !_cloudHasPalette)
            {
                point.R = point.TargetR;
                point.G = point.TargetG;
                point.B = point.TargetB;
                point.Mass = point.TargetMass;
            }
        }

        // Подложка под полем — среднее цветов обложки, притемнённое: между источниками не остаётся пустого места.
        _cloudBaseR = (float)(clusters.Sum(cluster => cluster.Red * cluster.Weight) / totalWeight * 0.55);
        _cloudBaseG = (float)(clusters.Sum(cluster => cluster.Green * cluster.Weight) / totalWeight * 0.55);
        _cloudBaseB = (float)(clusters.Sum(cluster => cluster.Blue * cluster.Weight) / totalWeight * 0.55);
        _cloudHasPalette = true;

        if (instant) RenderCloudFlowFrame(0);
    }

    // Размер битмапа подгоняется под пропорции окна; вызывается при смене размера и при включении фона.
    private void UpdateCloudBitmap()
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        int bitmapHeight = Math.Max(32, (int)Math.Round(CloudBitmapWidth * height / width));
        if (_cloudBitmap is null || bitmapHeight != _cloudHeight)
        {
            _cloudHeight = bitmapHeight;
            _cloudBitmap = new WriteableBitmap(CloudBitmapWidth, bitmapHeight, 96, 96, PixelFormats.Pbgra32, null);
            _cloudPixels = new byte[CloudBitmapWidth * bitmapHeight * 4];
            CloudsBackdrop.Source = _cloudBitmap;
        }

        RenderCloudFlowFrame(0);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(value, 0f, 255f);

    // Один кадр поля. dt — секунды с прошлого кадра для плавного перетекания цветов; dt = 0 сразу ставит целевые цвета.
    private void RenderCloudFlowFrame(double dt)
    {
        if (_cloudBitmap is null || CloudsBackdrop.Visibility != Visibility.Visible || ActualHeight <= 0) return;

        float blend = dt <= 0 ? 1f : (float)(1 - Math.Exp(-dt * CloudColorBlendPerSecond));
        int count = _cloudPoints.Length;
        Span<float> positionX = stackalloc float[count];
        Span<float> positionY = stackalloc float[count];
        Span<float> inverseSigmaSquared = stackalloc float[count];
        Span<float> mass = stackalloc float[count];
        Span<float> red = stackalloc float[count];
        Span<float> green = stackalloc float[count];
        Span<float> blue = stackalloc float[count];

        double time = _ambientMotionTime;
        for (int index = 0; index < count; index++)
        {
            CloudPoint point = _cloudPoints[index];
            point.R += (point.TargetR - point.R) * blend;
            point.G += (point.TargetG - point.G) * blend;
            point.B += (point.TargetB - point.B) * blend;
            point.Mass += (point.TargetMass - point.Mass) * blend;

            positionX[index] = (float)(point.CenterX + point.AmplitudeX * Math.Sin(time * point.FrequencyX + point.PhaseX));
            positionY[index] = (float)(point.CenterY + point.AmplitudeY * Math.Sin(time * point.FrequencyY + point.PhaseY));
            inverseSigmaSquared[index] = (float)(1.0 / (point.Sigma * point.Sigma));
            mass[index] = point.Mass;
            red[index] = point.R;
            green[index] = point.G;
            blue[index] = point.B;
        }

        int width = CloudBitmapWidth;
        int height = _cloudHeight;
        float aspect = (float)(ActualWidth / ActualHeight);
        byte[] pixels = _cloudPixels;
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                float sum = CloudBaseWeight;
                float r = _cloudBaseR * CloudBaseWeight;
                float g = _cloudBaseG * CloudBaseWeight;
                float b = _cloudBaseB * CloudBaseWeight;
                for (int index = 0; index < count; index++)
                {
                    float dx = (u - positionX[index]) * aspect;
                    float dy = v - positionY[index];
                    float falloff = 1f / (1f + (dx * dx + dy * dy) * inverseSigmaSquared[index]);
                    float weight = mass[index] * falloff * falloff * falloff;
                    sum += weight;
                    r += red[index] * weight;
                    g += green[index] * weight;
                    b += blue[index] * weight;
                }

                // Смесь разных цветов тускнеет, поэтому насыщенность вокруг яркости возвращается.
                float inverse = 1f / sum;
                r *= inverse;
                g *= inverse;
                b *= inverse;
                float luminance = 0.299f * r + 0.587f * g + 0.114f * b;
                pixels[offset++] = ToByte(luminance + (b - luminance) * CloudSaturation);
                pixels[offset++] = ToByte(luminance + (g - luminance) * CloudSaturation);
                pixels[offset++] = ToByte(luminance + (r - luminance) * CloudSaturation);
                pixels[offset++] = 255;
            }
        }

        _cloudBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
    }
}
