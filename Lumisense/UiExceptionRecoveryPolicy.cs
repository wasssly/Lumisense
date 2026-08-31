namespace Lumisense;

// Решает, можно ли безопасно оставить WPF-приложение работающим после исключения,
// дошедшего до DispatcherUnhandledException. Политика намеренно консервативна:
// неизвестные ошибки не скрываются, потому что могут означать повреждённое состояние UI
// или аудио-цепочки. Ожидаемые ошибки файлов, сети, формата и отмены должны по возможности
// обрабатываться рядом с операцией; этот класс остаётся последней защитой пользователя.
internal enum UiExceptionRecoveryAction
{
    Ignore,
    Continue,
    Terminate
}

internal static class UiExceptionRecoveryPolicy
{
    public static UiExceptionRecoveryAction Classify(Exception exception)
    {
        Exception root = Unwrap(exception);

        return root switch
        {
            OperationCanceledException => UiExceptionRecoveryAction.Ignore,
            System.IO.IOException => UiExceptionRecoveryAction.Continue,
            UnauthorizedAccessException => UiExceptionRecoveryAction.Continue,
            FormatException => UiExceptionRecoveryAction.Continue,
            System.IO.InvalidDataException => UiExceptionRecoveryAction.Continue,
            System.Net.Http.HttpRequestException => UiExceptionRecoveryAction.Continue,
            _ => UiExceptionRecoveryAction.Terminate
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            exception = aggregate.InnerExceptions[0];

        return exception;
    }
}
