using Lumisense;
using Xunit;

namespace Lumisense.Tests;

public sealed class UnavailableFilesTrackerTests
{
    [Fact]
    public void Update_ReportsOnlyFilesNotAnnouncedBefore()
    {
        var tracker = new UnavailableFilesTracker();
        var tracked = new[] { "a.mp3", "b.mp3", "c.mp3" };

        Assert.Equal(new[] { "a.mp3" }, tracker.Update(tracked, new[] { "a.mp3" }));
        // a.mp3 всё ещё недоступен, но о нём уже сообщали; новая пропажа — b.mp3.
        Assert.Equal(new[] { "b.mp3" }, tracker.Update(tracked, new[] { "a.mp3", "b.mp3" }));
        Assert.Empty(tracker.Update(tracked, new[] { "a.mp3", "b.mp3" }));
    }

    [Fact]
    public void Update_ForgetsReturnedFiles_SoTheyAreReportedAgainIfLostAgain()
    {
        var tracker = new UnavailableFilesTracker();
        var tracked = new[] { "a.mp3", "b.mp3" };

        tracker.Update(tracked, new[] { "a.mp3" });
        Assert.Empty(tracker.Update(tracked, new string[0]));

        Assert.Equal(new[] { "a.mp3" }, tracker.Update(tracked, new[] { "a.mp3" }));
    }

    [Fact]
    public void Update_ComparesPathsIgnoringCase()
    {
        var tracker = new UnavailableFilesTracker();

        tracker.Update(new[] { @"C:\Music\a.mp3" }, new[] { @"C:\Music\a.mp3" });

        Assert.Empty(tracker.Update(new[] { @"c:\music\A.mp3" }, new[] { @"c:\music\A.mp3" }));
    }

    [Fact]
    public void Update_DoesNotForgetFilesOfOtherFolders()
    {
        var tracker = new UnavailableFilesTracker();

        tracker.Update(new[] { "x.mp3" }, new[] { "x.mp3" });
        // Проверка другой папки (x.mp3 в ней не отслеживается) не должна «вернуть» x.mp3 и повторить уведомление.
        tracker.Update(new[] { "y.mp3" }, new string[0]);

        Assert.Empty(tracker.Update(new[] { "x.mp3" }, new[] { "x.mp3" }));
    }
}
