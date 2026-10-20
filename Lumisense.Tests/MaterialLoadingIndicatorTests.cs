using System.Linq;
using Xunit;

namespace Lumisense.Tests;

public sealed class MaterialLoadingIndicatorTests
{
    private const double Cycle = 0.65;

    [Fact]
    public void HasTheSevenShapesOfTheMaterialSequence() =>
        Assert.Equal(7, MaterialLoadingIndicator.ShapeCount);

    [Fact]
    public void ShapeAtTime_HoldsAShapeThenMorphsToTheNextAndLoops()
    {
        Assert.Equal((0, 0.0), MaterialLoadingIndicator.ShapeAtTime(0.0));
        Assert.Equal((0, 0.0), MaterialLoadingIndicator.ShapeAtTime(0.1));

        // Середина перетекания: 0,15 с покоя и ещё 0,25 с из 0,5 с.
        (int shape, double morph) = MaterialLoadingIndicator.ShapeAtTime(0.4);
        Assert.Equal(0, shape);
        Assert.Equal(0.5, morph, 6);

        Assert.Equal(1, MaterialLoadingIndicator.ShapeAtTime(Cycle + 0.01).Shape);

        // Полный круг из семи фигур возвращает к первой; небольшой запас убирает зависимость от округления.
        Assert.Equal(0, MaterialLoadingIndicator.ShapeAtTime(Cycle * MaterialLoadingIndicator.ShapeCount + 0.01).Shape);
        Assert.Equal((0, 0.0), MaterialLoadingIndicator.ShapeAtTime(-5.0));
    }

    [Fact]
    public void ComputeRadii_BlendsTheCurrentShapeIntoTheNextOne()
    {
        double[] first = MaterialLoadingIndicator.ComputeRadii(0, 0.0, 120);
        double[] next = MaterialLoadingIndicator.ComputeRadii(0, 1.0, 120);
        double[] half = MaterialLoadingIndicator.ComputeRadii(0, 0.5, 120);

        Assert.Equal(MaterialLoadingIndicator.ComputeRadii(1, 0.0, 120), next);
        for (int i = 0; i < half.Length; i++)
            Assert.Equal((first[i] + next[i]) / 2, half[i], 6);
    }

    [Fact]
    public void ComputeRadii_FirstShapeIsALobedBurstNotACircle()
    {
        double[] burst = MaterialLoadingIndicator.ComputeRadii(0, 0.0, 120);

        Assert.True(burst.Max() - burst.Min() > 0.1);
    }

    [Fact]
    public void ComputeRadii_NeverProducesTinyOrNegativeRadiiForAnyShape()
    {
        for (int shape = 0; shape < MaterialLoadingIndicator.ShapeCount; shape++)
        {
            foreach (double morph in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                double[] radii = MaterialLoadingIndicator.ComputeRadii(shape, morph, 120);
                Assert.True(radii.All(radius => double.IsFinite(radius) && radius > 0.4));
            }
        }
    }

    [Fact]
    public void PillShape_IsLongerThanItIsTall()
    {
        double[] pill = MaterialLoadingIndicator.ComputeRadii(3, 0.0, 120);

        // Угол 0 — вдоль длинной оси, угол 90° (индекс 30 из 120) — поперёк.
        Assert.True(pill[0] > pill[30] * 1.4);
    }
}
