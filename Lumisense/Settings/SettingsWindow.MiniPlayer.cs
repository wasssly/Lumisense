using System;
using System.Windows;

namespace Lumisense;

public partial class SettingsWindow
{
    // Вкладка «Полоса и ползунки» показывает, включён ли контур обложки и какого он вида, поэтому её надо обновлять при смене этих настроек.
    internal void ReloadProgressControls()
    {
        if (_lazyPages.TryGetValue("Progress", out ILazySettingsPage? page)) ((ProgressSettingsPage)page).ReloadProgressControls();
    }

    // Вызывается главным окном при смене трека, обложки, акцента и формы звука.
    public void RefreshProgressPreview()
    {
        if (_lazyPages.TryGetValue("Progress", out ILazySettingsPage? page)) ((ProgressSettingsPage)page).RefreshProgressPreview();
    }

    internal void MakeMiniArtworkProgressWavy()
    {
        EnsurePageBuilt("MiniPlayer");
        ((MiniPlayerSettingsPage)_lazyPages["MiniPlayer"]).MakeArtworkProgressWavy();
    }

    // Вызывается извне, когда закрепление, "поверх окон" или прозрачность переключили прямо на мини-плеере.
    public void RefreshMiniPlayerToggles()
    {
        if (_lazyPages.TryGetValue("MiniPlayer", out ILazySettingsPage? page)) ((MiniPlayerSettingsPage)page).RefreshMiniPlayerToggles();
    }

    internal void RefreshMiniArtworkProgressThicknessPresentation()
    {
        if (_lazyPages.TryGetValue("MiniPlayer", out ILazySettingsPage? page))
            ((MiniPlayerSettingsPage)page).RefreshMiniArtworkProgressThicknessPresentation();
    }
}
