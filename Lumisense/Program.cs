using System;
using System.Windows;
using Velopack;

namespace Lumisense;

/// <summary>Точка входа для Velopack: Run() должен выполниться до создания App и любого UI.</summary>
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
            // Запуск из IDE, старой Inno Setup-установки или portable-папки не должен блокироваться Velopack;
            // логгер ещё не инициализирован, поэтому без UI продолжаем прежний путь запуска.
            System.Diagnostics.Debug.WriteLine($"Velopack bootstrap skipped: {ex.Message}");
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
