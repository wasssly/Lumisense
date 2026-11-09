using System;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// LyricsService хранит выбор сервисов в статических полях: тест возвращает их, чтобы не влиять на остальные.
public sealed class LyricsExtraServiceSelectionTests : IDisposable
{
    private readonly AppSettings _original = new()
    {
        UseExtraLyricsSource = LyricsService.ExtraSourceEnabled,
        UseNetEaseLyrics = LyricsService.IsExtraSourceEnabled("NetEase"),
        UseQqMusicLyrics = LyricsService.IsExtraSourceEnabled("QQ Music"),
        UseKugouLyrics = LyricsService.IsExtraSourceEnabled("Kugou"),
        UsePaxsenixLyrics = LyricsService.IsExtraSourceEnabled("Paxsenix"),
    };

    public void Dispose() => LyricsService.ApplyExtraSourceSettings(_original);

    [Fact]
    public void ExtraServices_AreAllEnabledByDefault()
    {
        var settings = new AppSettings();

        Assert.True(settings.UseNetEaseLyrics);
        Assert.True(settings.UseQqMusicLyrics);
        Assert.True(settings.UseKugouLyrics);
        Assert.True(settings.UsePaxsenixLyrics);
    }

    [Fact]
    public void IsExtraSourceEnabled_ReflectsEachServiceCheckbox()
    {
        LyricsService.ApplyExtraSourceSettings(new AppSettings { UseExtraLyricsSource = true, UseKugouLyrics = false, UsePaxsenixLyrics = false });

        Assert.True(LyricsService.IsExtraSourceEnabled("NetEase"));
        Assert.True(LyricsService.IsExtraSourceEnabled("QQ Music"));
        Assert.False(LyricsService.IsExtraSourceEnabled("Kugou"));
        Assert.False(LyricsService.IsExtraSourceEnabled("Paxsenix"));
    }

    [Fact]
    public void IsExtraSourceEnabled_IsFalseForEveryServiceWhenMainSwitchIsOff()
    {
        LyricsService.ApplyExtraSourceSettings(new AppSettings { UseExtraLyricsSource = false });

        Assert.False(LyricsService.IsExtraSourceEnabled("NetEase"));
        Assert.False(LyricsService.IsExtraSourceEnabled("Paxsenix"));
    }
}
