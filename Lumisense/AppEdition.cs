namespace Lumisense;

// Не const: иначе в обычной сборке `if (AppEdition.IsLite)` даёт предупреждение о недостижимом коде.
internal static class AppEdition
{
    internal static bool IsLite { get; } =
#if LITE
        true;
#else
        false;
#endif
}
