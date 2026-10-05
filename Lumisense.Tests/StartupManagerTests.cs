using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class StartupManagerTests
{
    private const string Current = @"C:\Program Files\Lumisense\Lumisense.exe";

    [Fact]
    public void ShouldRepoint_WhenRegisteredFileIsGone()
    {
        Assert.True(StartupManager.ShouldRepoint("\"D:\\Old\\Lumisense.exe\"", Current, _ => false));
    }

    [Fact]
    public void ShouldRepoint_KeepsExistingOtherCopy()
    {
        Assert.False(StartupManager.ShouldRepoint("\"D:\\Portable\\Lumisense.exe\"", Current, _ => true));
    }

    [Fact]
    public void ShouldRepoint_IgnoresSamePathRegardlessOfCase()
    {
        Assert.False(StartupManager.ShouldRepoint("\"" + Current.ToUpperInvariant() + "\"", Current, _ => false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"unterminated")]
    public void ShouldRepoint_IgnoresMalformedValues(string runValue)
    {
        Assert.False(StartupManager.ShouldRepoint(runValue, Current, _ => false));
    }

    [Fact]
    public void ShouldRepoint_IgnoresUnknownCurrentPath()
    {
        Assert.False(StartupManager.ShouldRepoint("\"D:\\Old\\Lumisense.exe\"", null, _ => false));
    }
}
