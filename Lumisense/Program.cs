using System;
using System.Windows;
using Velopack;

namespace AudioPlayer;

/// <summary>
/// Явная точка входа WPF для Velopack. Run() должен выполняться до создания App и любого UI:
/// при install/update/uninstall hook Velopack выполняет fast-exit callback и завершает процесс,
/// поэтому обычная инициализация плеера ниже не запускается.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            VelopackApp.Build()
                // Штатный лог Velopack остаётся включённым; это добавляет безопасную копию
                // диагностических сообщений в журнал Lumisense для разбора update fallback.
                .SetLogger(new LumisenseVelopackLogger())
                // Обновление применяется только после явного действия пользователя в диалоге.
                // Это не допускает незаметной замены версии во время запуска.
                .SetAutoApplyOnStartup(false)
                // Hook лишь создаёт marker. Никаких диалогов, удаления legacy Inno Setup или
                // обращения к настройкам здесь нет: Run() должен быстро завершать lifecycle path.
                .OnFirstRun(version => VelopackMigrationLifecycle.MarkFirstVelopackRun(version.ToString()))
                .Run();
        }
        catch (Exception ex)
        {
            // При запуске из IDE, старой Inno Setup-установки или обычной portable-папки
            // Velopack не должен блокировать запуск. Логгер ещё не инициализирован, поэтому
            // не показываем UI и продолжаем старый путь запуска.
            System.Diagnostics.Debug.WriteLine($"Velopack bootstrap skipped: {ex.Message}");
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
