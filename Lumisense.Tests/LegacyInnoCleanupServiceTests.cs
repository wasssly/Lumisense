using AudioPlayer;
using System.IO;
using Xunit;

namespace Lumisense.Tests;

public sealed class LegacyInnoCleanupServiceTests
{
    [Fact]
    public void GetVerifiedUninstallerPath_AcceptsOnlyQuotedExistingInnoUninstaller()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Lumisense.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string uninstaller = Path.Combine(directory, "unins000.exe");
        File.WriteAllBytes(uninstaller, []);

        try
        {
            string? result = LegacyInnoCleanupService.GetVerifiedUninstallerPath($"\"{uninstaller}\"");

            Assert.Equal(uninstaller, result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetVerifiedUninstallerPath_RejectsQuotedUninstallerThatDoesNotExist()
    {
        string nonExistentUninstaller = Path.Combine(
            Path.GetTempPath(),
            "Lumisense.Tests",
            Guid.NewGuid().ToString("N"),
            "unins000.exe");

        Assert.False(File.Exists(nonExistentUninstaller));
        Assert.Null(LegacyInnoCleanupService.GetVerifiedUninstallerPath($"\"{nonExistentUninstaller}\""));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Lumisense\\unins000.exe\" /SILENT")]
    [InlineData("C:\\Program Files\\Lumisense\\unins000.exe")]
    [InlineData("\"C:\\Program Files\\Lumisense\\Lumisense.exe\"")]
    public void GetVerifiedUninstallerPath_RejectsArgumentsUnquotedOrNonUninstallerPaths(string value)
    {
        Assert.Null(LegacyInnoCleanupService.GetVerifiedUninstallerPath(value));
    }
}
