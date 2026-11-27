namespace Lumisense;

internal static class BackgroundTask
{
    // Оборачивает fire-and-forget async-вызовы логированием исключения сразу: TaskScheduler.UnobservedTaskException
    // (App.xaml.cs) сработает лишь после сборки мусора, а иногда и вовсе не успеет до закрытия процесса.
    // Обработчики событий вместо async void вызывают XxxAsync через этот метод: тогда исключение попадает в лог, а не в Dispatcher.
    public static async void FireAndForget(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка в фоновой операции \"{operationName}\"", ex);
        }
    }
}
