using System;
using Lumisense;
using NAudio.Wave;
using Xunit;

namespace Lumisense.Tests;

public sealed class ParametricEqTests
{
    private const string AutoEqText = """
        Preamp: -6.2 dB
        Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70
        Filter 2: ON PK Fc 2500 Hz Gain -3,5 dB Q 1.41
        Filter 3: OFF PK Fc 1000 Hz Gain 2.0 dB Q 1.00
        Filter 4: ON HSC Fc 10000 Hz Gain -2.0 dB Q 0.70
        Filter 5: ON HP Fc 20 Hz
        """;

    [Fact]
    public void Parse_ReadsPreampAndSupportedFilters()
    {
        ParametricEqProfile? profile = ParametricEqProfile.Parse(AutoEqText);

        Assert.NotNull(profile);
        Assert.Equal(-6.2, profile!.PreampDb, 3);
        Assert.Equal(3, profile.Filters.Count);
        Assert.Equal(new ParametricFilter(ParametricFilterType.LowShelf, 105, 5.5, 0.70), profile.Filters[0]);
        Assert.Equal(-3.5, profile.Filters[1].GainDb, 3);
        Assert.Equal(ParametricFilterType.HighShelf, profile.Filters[2].Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("Filter 1: OFF PK Fc 1000 Hz Gain 2.0 dB Q 1.00")]
    public void Parse_ReturnsNullWithoutUsableFilters(string? text) =>
        Assert.Null(ParametricEqProfile.Parse(text));

    [Fact]
    public void PeakingFilter_BoostsCenterFrequency()
    {
        var filter = new ParametricFilter(ParametricFilterType.Peaking, 1000, 6, 1.0);

        double db = MagnitudeDb(ParametricEqSampleProvider.Coefficients(filter, 48000), 1000, 48000);

        Assert.InRange(db, 5.9, 6.1);
    }

    [Fact]
    public void LowShelf_BoostsBassAndKeepsTreble()
    {
        var filter = new ParametricFilter(ParametricFilterType.LowShelf, 200, 6, 0.707);
        var c = ParametricEqSampleProvider.Coefficients(filter, 48000);

        Assert.InRange(MagnitudeDb(c, 20, 48000), 5.5, 6.1);
        Assert.InRange(MagnitudeDb(c, 12000, 48000), -0.2, 0.2);
    }

    [Fact]
    public void HighShelf_CutsTrebleAndKeepsBass()
    {
        var filter = new ParametricFilter(ParametricFilterType.HighShelf, 4000, -6, 0.707);
        var c = ParametricEqSampleProvider.Coefficients(filter, 48000);

        Assert.InRange(MagnitudeDb(c, 20000, 48000), -6.1, -5.0);
        Assert.InRange(MagnitudeDb(c, 100, 48000), -0.2, 0.2);
    }

    [Fact]
    public void Provider_AppliesPreampOnlyWhenEnabled()
    {
        ParametricEqProfile profile = ParametricEqProfile.Parse("Preamp: -6 dB\nFilter 1: ON PK Fc 1000 Hz Gain 0 dB Q 1.0")!;
        var source = new ConstantSource(0.5f, 48000);
        var provider = new ParametricEqSampleProvider(source) { Profile = profile };
        float[] disabled = new float[4800];
        provider.Read(disabled);
        Assert.All(disabled, sample => Assert.Equal(0.5f, sample));

        provider.Enabled = true;
        float[] enabled = new float[48000];
        provider.Read(enabled);

        Assert.Equal(0.5f * (float)Math.Pow(10, -6 / 20.0), enabled[^1], 3);
    }

    private static double MagnitudeDb((double B0, double B1, double B2, double A1, double A2) c, double frequency, int sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate;
        var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
        var z2 = z1 * z1;
        var numerator = c.B0 + c.B1 * z1 + c.B2 * z2;
        var denominator = 1 + c.A1 * z1 + c.A2 * z2;
        return 20 * Math.Log10((numerator / denominator).Magnitude);
    }

    private sealed class ConstantSource(float value, int sampleRate) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(Span<float> buffer)
        {
            buffer.Fill(value);
            return buffer.Length;
        }
    }
}
