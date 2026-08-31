using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class LegacyIntegrationRepairServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Lumisense\\Lumisense.exe\"", "C:\\Program Files\\Lumisense\\Lumisense.exe")]
    [InlineData("C:\\Lumisense\\Lumisense.exe", "C:\\Lumisense\\Lumisense.exe")]
    [InlineData("C:\\Lumisense\\Lumisense.exe --minimized", "C:\\Lumisense\\Lumisense.exe")]
    public void ExtractExecutablePath_HandlesQuotedAndUnquotedRunValues(string runValue, string expected)
    {
        string? actual = LegacyIntegrationRepairService.ExtractExecutablePath(runValue);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void ExtractExecutablePath_ReturnsNullForEmptyOrMalformedValues(string runValue)
    {
        string? actual = LegacyIntegrationRepairService.ExtractExecutablePath(runValue);

        Assert.Null(actual);
    }

    [Fact]
    public void IsUnderDirectory_ReturnsTrueForFileInsideLegacyInstallDirectory()
    {
        bool result = LegacyIntegrationRepairService.IsUnderDirectory(
            @"C:\Program Files (x86)\Lumisense\Lumisense.exe",
            @"C:\Program Files (x86)\Lumisense");

        Assert.True(result);
    }

    [Fact]
    public void IsUnderDirectory_ReturnsFalseForFileOutsideLegacyInstallDirectory()
    {
        bool result = LegacyIntegrationRepairService.IsUnderDirectory(
            @"C:\Program Files\Lumisense\Lumisense.exe",
            @"C:\Program Files (x86)\Lumisense");

        Assert.False(result);
    }

    [Fact]
    public void IsUnderDirectory_DoesNotMatchDifferentDirectoryWithSharedPrefix()
    {
        // "Lumisense" не должен считаться под "Lumisense2" просто из-за общего строкового
        // префикса — сравнение обязано учитывать границу каталога.
        bool result = LegacyIntegrationRepairService.IsUnderDirectory(
            @"C:\Program Files\Lumisense2\Lumisense.exe",
            @"C:\Program Files\Lumisense");

        Assert.False(result);
    }
}
