using System;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Тестовый процесс не установлен через Velopack, поэтому проверяется безопасный legacy-режим: без исключений и без окон.
public sealed class UpdateMigrationGuardTests
{
    [Fact]
    public void IsVelopackManagedInstall_OutsideInstalledApp_IsFalse()
    {
        Assert.False(UpdateMigrationGuard.IsVelopackManagedInstall());
    }

    [Fact]
    public void LogCurrentMode_OutsideInstalledApp_DoesNotThrow()
    {
        var exception = Record.Exception(UpdateMigrationGuard.LogCurrentMode);

        Assert.Null(exception);
    }

    [Fact]
    public void TryShowFirstRunNotice_OutsideInstalledApp_ReturnsWithoutShowingAnything()
    {
        var exception = Record.Exception(UpdateMigrationGuard.TryShowFirstRunNotice);

        Assert.Null(exception);
    }
}
