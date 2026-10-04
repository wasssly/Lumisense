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

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void SliderMaterialConverter_OverrideBeatsGlobalSetting(bool global, bool? forced, bool expected)
    {
        object second = forced.HasValue ? forced.Value : System.Windows.DependencyProperty.UnsetValue;

        object result = SliderMaterialConverter.Instance.Convert(
            new object[] { global, second }, typeof(bool), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }
}
