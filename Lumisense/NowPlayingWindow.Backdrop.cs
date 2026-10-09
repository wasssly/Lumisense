using System.Windows;

namespace Lumisense;

// Выбор фона Now Playing (AppSettings.NowPlayingBackground): "Clouds" — прежние облака из палитры обложки (по умолчанию),
// "Orbs" — мягкие цветные шары на орбитах, "Waves" — размытые волны. Рисование шаров и волн — в NowPlayingWindow.Dynamic.cs.
public partial class NowPlayingWindow
{
    private const string OrbsBackgroundMode = "Orbs";
    private const string WavesBackgroundMode = "Waves";

    // Любое неизвестное значение (в том числе удалённый режим «Cover») трактуется как «Облака».
    private string BackgroundMode => _owner.Settings.NowPlayingBackground is OrbsBackgroundMode or WavesBackgroundMode
        ? _owner.Settings.NowPlayingBackground : "Clouds";

    private bool IsOrbsBackground => BackgroundMode == OrbsBackgroundMode;

    private bool IsWavesBackground => BackgroundMode == WavesBackgroundMode;

    // Применяет выбранный режим фона. Вызывается при создании окна и из MainWindow при смене настройки.
    internal void ApplyBackgroundMode()
    {
        string mode = BackgroundMode;
        CloudsBackdrop.Visibility = mode == "Clouds" ? Visibility.Visible : Visibility.Collapsed;
        OrbsBackdrop.Visibility = mode == OrbsBackgroundMode ? Visibility.Visible : Visibility.Collapsed;
        WavesBackdrop.Visibility = mode == WavesBackgroundMode ? Visibility.Visible : Visibility.Collapsed;

        if (mode == OrbsBackgroundMode) EnsureOrbs();
        ApplyDynamicPalette(_dynamicPalette, animate: false);

        UpdateBackdropGeometry();
        // Таймер облаков нужен только режиму «Облака»; шары и волны двигаются своим покадровым расчётом.
        UpdateAmbientAnimation(_owner.IsPlayingNow);
    }
}
