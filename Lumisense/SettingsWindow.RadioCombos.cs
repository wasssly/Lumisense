using System.Windows;

namespace Lumisense;

// Выпадающие списки радиогрупп страниц, разобранных вместе с окном; ленивые страницы связывают свои сами (RadioComboLinks).
public partial class SettingsWindow
{
    private void InitializeRadioCombos() => RadioComboLinks.Initialize(this, FindName);
}
