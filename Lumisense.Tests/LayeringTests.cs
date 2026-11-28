using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Lumisense.Tests;

// Нижние слои не должны знать о настройках и окнах: иначе зависимости снова замкнутся в цикл через AppSettings.
public sealed partial class LayeringTests
{
    private static readonly string[] ForbiddenInLowLayers = ["AppSettings", "SettingsManager", "SettingsWindow", "MainWindow", "ISettingsHost"];

    [Theory]
    [InlineData("Audio")]
    [InlineData("Localization")]
    public void LowLayer_DoesNotReferenceSettingsOrWindows(string folder)
    {
        string root = SourceFolder(folder);
        if (!Directory.Exists(root)) return; // тесты запущены без исходников приложения

        var violations = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string code = StringLiteral().Replace(LineComment().Replace(File.ReadAllText(path), ""), "\"\"");
            foreach (string name in ForbiddenInLowLayers)
            {
                if (Regex.IsMatch(code, $@"\b{name}\b"))
                    violations.Add($"{folder}/{Path.GetFileName(path)}: {name}");
            }
        }

        Assert.True(violations.Count == 0, "Нижний слой зависит от верхнего:\n" + string.Join("\n", violations));
    }

    private static string SourceFolder(string folder, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Lumisense", folder));

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();
}
