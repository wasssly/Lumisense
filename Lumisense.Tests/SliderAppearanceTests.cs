using Xunit;

namespace Lumisense.Tests;

public class SliderAppearanceTests
{
    [Theory]
    [InlineData("Material", true)]
    [InlineData("Default", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Apply_SetsIsMaterialOnlyForMaterial(string? style, bool expected)
    {
        SliderAppearance.Instance.Apply(style);

        Assert.Equal(expected, SliderAppearance.Instance.IsMaterial);
        SliderAppearance.Instance.Apply("Default");
    }

    [Fact]
    public void Apply_RaisesPropertyChangedOnlyWhenValueChanges()
    {
        int raised = 0;
        SliderAppearance.Instance.Apply("Default");
        SliderAppearance.Instance.PropertyChanged += (_, _) => raised++;

        SliderAppearance.Instance.Apply("Default");
        SliderAppearance.Instance.Apply("Material");
        SliderAppearance.Instance.Apply("Material");
        SliderAppearance.Instance.Apply("Default");

        Assert.Equal(2, raised);
    }
}
