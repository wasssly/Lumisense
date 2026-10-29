using System.Linq;
using Xunit;

namespace Lumisense.Tests;

public sealed class MaterialLoadingIndicatorTests
{
    private const double Cycle = 0.65;
    private const int Points = MaterialLoadingIndicator.SamplePoints;

    [Fact]
    public void HasTheSevenShapesOfTheMaterialSequence() =>
        Assert.Equal(7, MaterialLoadingIndicator.ShapeCount);

    [Fact]
    public void SpringProgress_RisesToOneInsideTheCycleAndStaysThere()
    {
        Assert.Equal(0.0, MaterialLoadingIndicator.SpringProgress(0.0), 6);

        double previous = 0.0;
        foreach (double seconds in new[] { 0.05, 0.1, 0.15, 0.2 })
        {
            double value = MaterialLoadingIndicator.SpringProgress(seconds);
            Assert.True(value > previous);
            previous = value;
        }

        Assert.Equal(1.0, MaterialLoadingIndicator.SpringProgress(0.3), 6);
        Assert.Equal(1.0, MaterialLoadingIndicator.SpringProgress(Cycle - 0.01), 6);
    }

    [Fact]
    public void ShapeAtTime_MorphsToTheNextShapeThenAdvancesAndLoops()
    {
        Assert.Equal((0, 0.0), MaterialLoadingIndicator.ShapeAtTime(0.0));
        Assert.Equal((0, 0.0), MaterialLoadingIndicator.ShapeAtTime(-5.0));

        (int shape, double morph) = MaterialLoadingIndicator.ShapeAtTime(0.1);
        Assert.Equal(0, shape);
        Assert.InRange(morph, 0.1, 0.9);

        Assert.Equal((0, 1.0), MaterialLoadingIndicator.ShapeAtTime(Cycle - 0.01));
        Assert.Equal(1, MaterialLoadingIndicator.ShapeAtTime(Cycle + 0.01).Shape);

        // Небольшой запас убирает зависимость от округления на границе цикла.
        Assert.Equal(0, MaterialLoadingIndicator.ShapeAtTime(Cycle * MaterialLoadingIndicator.ShapeCount + 0.01).Shape);
    }

    [Fact]
    public void ComputeRadii_BlendsTheCurrentShapeIntoTheNextOne()
    {
        double[] first = MaterialLoadingIndicator.ComputeRadii(0, 0.0);
        double[] next = MaterialLoadingIndicator.ComputeRadii(0, 1.0);
        double[] half = MaterialLoadingIndicator.ComputeRadii(0, 0.5);

        Assert.Equal(MaterialLoadingIndicator.ComputeRadii(1, 0.0), next);
        for (int i = 0; i < half.Length; i++)
            Assert.Equal((first[i] + next[i]) / 2, half[i], 6);
    }

    [Fact]
    public void ComputeRadii_FirstShapeIsALobedBurstNotACircle()
    {
        double[] burst = MaterialLoadingIndicator.ComputeRadii(0, 0.0);

        Assert.True(burst.Max() - burst.Min() > 0.05);
    }

    [Fact]
    public void ComputeRadii_NeverProducesTinyOrNegativeRadiiForAnyShape()
    {
        for (int shape = 0; shape < MaterialLoadingIndicator.ShapeCount; shape++)
        {
            foreach (double morph in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                double[] radii = MaterialLoadingIndicator.ComputeRadii(shape, morph);
                Assert.True(radii.All(radius => double.IsFinite(radius) && radius > 0.3));
            }
        }
    }

    [Fact]
    public void EveryShapeFitsTheSharedBoundingCircle()
    {
        for (int shape = 0; shape < MaterialLoadingIndicator.ShapeCount; shape++)
            Assert.True(MaterialLoadingIndicator.ComputeRadii(shape, 0.0).Max() <= MaterialLoadingIndicator.MaxShapeRadius + 1e-9);

        // Габарит нормирован на 1, поэтому дальняя точка от центра не меньше половины.
        Assert.InRange(MaterialLoadingIndicator.MaxShapeRadius, 0.5, 1.0);
    }

    [Fact]
    public void PillShape_IsStretchedAlongTheDiagonal()
    {
        double[] pill = MaterialLoadingIndicator.ComputeRadii(3, 0.0);

        // Пилюля Material вытянута по диагонали: 135° (конец) дальше, чем 45° (бок).
        Assert.True(pill[Points * 135 / 360] > pill[Points * 45 / 360] * 1.15);
    }
}
