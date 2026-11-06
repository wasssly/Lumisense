using System.Windows;

namespace Lumisense;

public partial class SettingsWindow
{
    // Лайт не содержит Now Playing, статистику, остров и Discord: убираем их настройки и то, что имеет смысл только для них.
    private void HideNowPlayingAndStatisticsSettings()
    {
        NavCoverText.Text = LocalizationService.Translate("Обложка");
        CoverPageTitle.Text = LocalizationService.Translate("Обложка");
        CoverPageDescription.Text = LocalizationService.Translate("Обложка, избранное и подпись под названием трека.");
        NowPlayingBackgroundCard.Visibility = Visibility.Collapsed;

        AnimationNowPlayingCheckBox.Visibility = Visibility.Collapsed;
        AnimationIslandCheckBox.Visibility = Visibility.Collapsed;
        ProgressSurfaceNowPlayingRadio.Visibility = Visibility.Collapsed;

        BackdropBaseHelpText.Text = LocalizationService.Translate("Основа применяется к главному окну и окну настроек.");
        LyricsEffectHelpText.Text = LocalizationService.Translate(
            "Активная строка остаётся белой, неактивные — приглушённо-серыми. Свечение применяется в панели текста главного окна.");
        LyricsAlignmentHelpText.Text = LocalizationService.Translate(
            "Относится к тексту песни в панели главного окна, и к синхронному, и к обычному.");

        // Подложка, полоска, увеличение, приглушение, размытие и число видимых строк рисует только Now Playing.
        foreach (UIElement element in new UIElement[]
                 {
                     SyncedLyricsEffectGlowPillRadio, SyncedLyricsEffectPillRadio, SyncedLyricsEffectBarRadio,
                     SyncedLyricsScaleCheckBox, SyncedLyricsFadeCheckBox, SyncedLyricsBlurCheckBox
                 })
            element.Visibility = Visibility.Collapsed;

        foreach (System.Windows.Controls.Slider slider in new System.Windows.Controls.Slider[]
                 {
                     NowPlayingLyricsFontSizeSlider, SyncedLyricsVisibleLinesSlider, SyncedLyricsFadeStrengthSlider,
                     SyncedLyricsScalePercentSlider, SyncedLyricsBlurStrengthSlider
                 })
            CollapseSliderRow(slider);

        foreach (System.Windows.Controls.ComboBox combo in ModernSlotCombos())
        {
            for (int i = combo.Items.Count - 1; i >= 0; i--)
            {
                if (combo.Items[i] is System.Windows.Controls.ComboBoxItem { Tag: "Statistics" }) combo.Items.RemoveAt(i);
            }
        }
    }

    // Строка ползунка: подпись над ним, сам ползунок и поле значения.
    private static void CollapseSliderRow(System.Windows.Controls.Slider slider)
    {
        if (slider.Parent is not System.Windows.Controls.Grid track || track.Parent is not System.Windows.Controls.Grid row || row.Parent is not System.Windows.Controls.Panel panel) return;

        int index = panel.Children.IndexOf(row);
        row.Visibility = Visibility.Collapsed;
        if (index > 0 && panel.Children[index - 1] is System.Windows.Controls.TextBlock label) label.Visibility = Visibility.Collapsed;
    }

    // Записи поиска для скрытых в Лайте настроек: страница острова, свёрнутые карточки и строки.
    private void PruneSearchIndexForLite()
    {
        _searchIndex.RemoveAll(entry => entry.PageKey == "Island" || IsCollapsedBelowPage(entry.Target));
        for (int i = 0; i < _searchIndex.Count; i++)
        {
            if (_searchIndex[i].PageTitle == "Обложка и Now Playing")
                _searchIndex[i] = _searchIndex[i] with { PageTitle = "Обложка" };
        }
    }

    private static bool IsCollapsedBelowPage(DependencyObject? node)
    {
        while (node is FrameworkElement element && !element.Name.StartsWith("Page", StringComparison.Ordinal))
        {
            if (element.Visibility == Visibility.Collapsed) return true;
            node = element.Parent;
        }

        return false;
    }
}
