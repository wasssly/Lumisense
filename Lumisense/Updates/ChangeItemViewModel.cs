using System.ComponentModel;
using System.Windows.Media;

namespace Lumisense;

// Обёртка над ChangeItem для UI: помимо текста сразу несёт готовые для биндинга
// подпись типа, ключ иконки и цвет — см. ChangeTypeCatalog
public sealed class ChangeItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text { get; private set; }
    // Исходный русский текст пункта: по нему ищется перевод и запрашивается автоперевод.
    public string SourceText { get; }
    public bool IsMachineTranslated { get; private set; }
    // Подсказка у пунктов с автоматическим переводом; у остальных её нет (null — подсказка не показывается).
    public string? MachineTranslationToolTip => IsMachineTranslated
        ? LocalizationService.Get(LocalizationKey.ChangelogMachineTranslated)
        : null;
    public string TypeKey { get; }
    public string TypeLabel { get; }
    public string IconKey { get; }
    public SolidColorBrush TypeBrush { get; }

    public ChangeItemViewModel(ChangeItem source)
    {
        var info = ChangeTypeCatalog.Resolve(source.Type);

        SourceText = source.Text;
        Text = ChangelogTranslationCatalog.Translate(source.Text, LocalizationService.IsEnglish);
        IsMachineTranslated = ChangelogTranslationCatalog.IsMachineTranslated(source.Text, LocalizationService.IsEnglish);
        TypeKey = info.Key;
        TypeLabel = info.Label;
        IconKey = info.IconKey;

        TypeBrush = new SolidColorBrush(info.Color);
        TypeBrush.Freeze();
    }

    // Подхватывает перевод, который появился после создания (автоперевод идёт в фоне). Окно журнала обновляет пункты на месте,
    // а не пересоздаёт список: иначе повторный выбор версии сбрасывал бы прокрутку наверх.
    public void RefreshTranslation()
    {
        bool english = LocalizationService.IsEnglish;
        string text = ChangelogTranslationCatalog.Translate(SourceText, english);
        bool machine = ChangelogTranslationCatalog.IsMachineTranslated(SourceText, english);
        if (text == Text && machine == IsMachineTranslated) return;

        Text = text;
        IsMachineTranslated = machine;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMachineTranslated)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MachineTranslationToolTip)));
    }
}
