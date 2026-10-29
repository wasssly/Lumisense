using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Сканирует .xaml и проверяет, что у каждой русской подписи (Text, Content, ToolTip, Header, Title, PlaceholderText)
// есть английский перевод в словаре: LocalizationService.Apply переводит разметку по точному совпадению строки,
// и опечатка или правка русской фразы без правки словаря иначе давала бы непереведённый текст молча.
// Дополняет LocalizationLiteralsTests, который проверяет только вызовы Translate/Format в .cs.
public sealed class LocalizationXamlLiteralsTests
{
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "ToolTip", "Header", "Title", "PlaceholderText"
    };

    // Подписи, которых нет в словаре на момент добавления теста. Часть из них заменяется из кода или из LocalizationResources
    // (например, «Приостановить» у загрузки обновления), часть, возможно, остаётся без перевода. Новые строки сюда не добавлять:
    // добавьте перевод в LocalizationService. Если запись из списка переведена, удалите её отсюда.
    private static readonly HashSet<string> KnownWithoutDictionaryEntry = new(StringComparer.Ordinal)
    {
        "Lumisense периодически проверяет, не развёрнута ли сейчас игра на весь экран и не запущен ли известный оверлей (RTSS, Xbox Game Bar, NVIDIA/AMD оверлей), и на время включает режим совместимости сам. Определение эвристическое и не гарантирует 100% точности — не задетектит игру в оконном режиме или незнакомый оверлей.",
        "MP3-копия с текущими скоростью и тоном",
        "В очереди: 0",
        "Виниловый режим делает обложку круглой и приостанавливает её вращение вместе с треком. Круглый режим оставляет обложку неподвижной.",
        "Включать автоматически при обнаружении игры или оверлея",
        "Все иконки — Duotone",
        "Выбор второй кнопки",
        "Держать включённым всегда",
        "Держит режим совместимости включённым постоянно, независимо от автоопределения выше — на случай, если игру или оверлей не удалось обнаружить автоматически. Не обязателен, если автоопределение справляется само.",
        "Добровольно скачайте полный пакет текущей MSI-версии один раз. Он станет локальной базой, и следующие совместимые обновления смогут использовать значительно меньшие delta-пакеты. Плеер не будет обновлён или перезапущен.",
        "Значок приложения",
        "ИНСТРУМЕНТЫ",
        "Изменение масштаба интерфейса",
        "Измерить отклик и скорость небольшого безопасного фрагмента release-файла",
        "Источники:",
        "Классический",
        "Контекстное меню мини-плеера",
        "Круглая (без вращения)",
        "МИНИ-ПЛЕЕР И УВЕДОМЛЕНИЯ",
        "Мягкие, двухцветные",
        "Набор иконок",
        "Нажмите, чтобы ввести значение с клавиатуры",
        "Недоступные: 0",
        "Новый масштаб будет применён после перезапуска Lumisense. Текущая работа плеера не будет прервана, если выбрать «Позже».",
        "Обложка ищется автоматически по названию и исполнителю через открытые каталоги (iTunes, Deezer, MusicBrainz) и показывается только по прямой HTTPS-ссылке — сам файл с диска в Discord не загружается. Действует только вместе с «Показывать название и исполнителя».",
        "Оригинальный значок Lumisense",
        "Отключенные пункты не показываются по ПКМ на мини-плеере.",
        "Открыть мастер удаления",
        "Отменить загрузку",
        "Перезапустить",
        "Перейти",
        "Плоский, сиреневый",
        "Плотный, контрастный",
        "Подготовить компактные обновления",
        "Поиск по названию",
        "Показывать обложку трека",
        "Применяется сразу. ПКМ по карточке — посмотреть весь набор.",
        "Приостановить",
        "Проверить зеркала",
        "Прогресс вокруг обложки",
        "Расположение кнопок",
        "Скачать базовый пакет",
        "Скорость и тон воспроизведения",
        "Совместимость с играми и оверлеем",
        "Сохранить обработанную копию…",
        "Сплошные силуэты",
        "Стеклянный, синий",
        "Тонкий контур, round cap",
        "Удалите прежнюю Inno Setup-установку после перехода на MSI. В мастере удаления выберите «Нет» при вопросе об удалении пользовательских данных.",
        "Удалить старую EXE-копию",
        "Ультратонкая линия",
        "Упрощает рендеринг плавающих окон для снижения риска сбоев Steam Overlay"
    };

    private static string ProjectDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "Lumisense"));

    private static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u04FF';

    [Fact]
    public void EveryRussianXamlLiteral_HasAnEnglishTranslation_OrIsKnownGap()
    {
        string directory = ProjectDirectory();
        if (!Directory.Exists(directory))
            return; // тесты запущены без исходников приложения: сканировать нечего

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int checkedLiterals = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal))
                continue;

            XDocument document = XDocument.Load(file);
            foreach (XAttribute attribute in document.Descendants().SelectMany(element => element.Attributes()))
            {
                if (!Attributes.Contains(attribute.Name.LocalName))
                    continue;

                string value = attribute.Value;
                // Привязки и расширения разметки ({Binding ...}) не литералы; строки без кириллицы переводить нечего.
                if (value.StartsWith('{') || !value.Any(IsCyrillic))
                    continue;

                checkedLiterals++;
                if (!LocalizationService.HasEnglishTranslation(value) && !KnownWithoutDictionaryEntry.Contains(value))
                    missing.Add(Path.GetFileName(file) + ": " + value);
            }
        }

        Assert.True(checkedLiterals > 300, $"Нашлось слишком мало строк для проверки ({checkedLiterals}): путь к исходникам или разбор XAML сломан.");
        Assert.Equal(new List<string>(), missing.ToList());
    }
}
