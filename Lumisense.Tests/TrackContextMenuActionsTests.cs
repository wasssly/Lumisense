using System.Collections.Generic;
using AudioPlayer;
using Xunit;

namespace Lumisense.Tests;

public sealed class TrackContextMenuActionsTests
{
    [Fact]
    public void NormalizeDisabledActions_PreservesFindFileAction()
    {
        var normalized = TrackContextMenuActions.NormalizeDisabledActions(new[]
        {
            "findfile",
            TrackContextMenuActions.FindFile,
            "unknown-action"
        });

        Assert.Equal(new List<string> { TrackContextMenuActions.FindFile }, normalized);
    }
}
