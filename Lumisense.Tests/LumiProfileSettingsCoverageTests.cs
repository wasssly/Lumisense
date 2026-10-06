using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Lumisense;
using Xunit;

namespace Lumisense.Tests;

// Защита от забытых настроек: каждое свойство AppSettings либо переносится профилем (импорт и «Сбросить настройки»),
// либо явно перечислено ниже как не переносимое. Новое свойство без решения ломает этот тест.
public sealed class LumiProfileSettingsCoverageTests
{
    // Пользовательские данные и состояние работы: профиль их не несёт, экспорт их очищает.
    private static readonly HashSet<string> UserDataAndRuntimeState = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.SavedPlaylistFolders), nameof(AppSettings.SavedPlaylist),
        nameof(AppSettings.FavoriteTracks), nameof(AppSettings.PinnedFavoriteTracks),
        nameof(AppSettings.SavedQueue), nameof(AppSettings.ShuffleHistory), nameof(AppSettings.ShuffleHistoryIndex), nameof(AppSettings.ShuffleBag),
        nameof(AppSettings.LastTrackPath), nameof(AppSettings.LastPositionSeconds),
        nameof(AppSettings.WasPlayingOnClose), nameof(AppSettings.WasMiniPlayerOnClose),
        nameof(AppSettings.PlayCounts), nameof(AppSettings.TotalListenSeconds), nameof(AppSettings.StatsStartedAt)
    };

    // Не переносятся по другим причинам (причина указана рядом).
    private static readonly HashSet<string> NotTransferable = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.SettingsSchemaVersion),                       // версия формата файла, ставит SettingsManager
        nameof(AppSettings.ForwardCompatibleProperties),                 // неизвестные поля из более новых версий
        nameof(AppSettings.SaveQueueBetweenRestarts),                    // сброс настроек его не трогает (решение 0026)
        nameof(AppSettings.SkippedUpdateVersion),                        // относится к этой установке
        nameof(AppSettings.UpdateMirrorWarningSuppressed),               // подтверждение пользователя на этой установке
        nameof(AppSettings.HklmWildcardContextMenuCleanupAttempted),     // служебная отметка этой машины
        nameof(AppSettings.TrackLoadTraceEnabled),                       // локальная диагностика
        nameof(AppSettings.EqualizerPresets)                             // у пресетов свой импорт и экспорт (.json)
    };

    private static IEnumerable<PropertyInfo> AllSettingsProperties() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // Списки строк (например, отключённые пункты меню) после нормализации идут в другом порядке, но это те же наборы.
    private static bool SameValue(object? left, object? right) =>
        left is List<string> leftList && right is List<string> rightList
            ? leftList.OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(rightList.OrderBy(item => item, StringComparer.Ordinal))
            : Json(left) == Json(right);

    private static object? DifferentValue(PropertyInfo property, AppSettings defaults)
    {
        object? current = property.GetValue(defaults);
        string name = property.Name;

        // Значения, которые Apply принимает только из допустимого набора.
        switch (name)
        {
            case nameof(AppSettings.IconPack): return IconPacks.All.First(pack => pack != (string?)current);
            case nameof(AppSettings.AppIcon): return AppIcons.All.First(icon => icon != (string?)current);
            case nameof(AppSettings.WasapiMode): return current as string == "Shared" ? "Exclusive" : "Shared";
            case nameof(AppSettings.TrackChangeToastArtSide): return current as string == "Left" ? "Right" : "Left";
            case nameof(AppSettings.TrackChangeToastTextAlignment): return current as string == "Left" ? "Right" : "Left";
            case nameof(AppSettings.LyricsTextAlignment): return current as string == "Left" ? "Right" : "Left";
            case nameof(AppSettings.FavoriteHeartAnimation): return current as string == "Fill" ? "Ring" : "Fill";
            case nameof(AppSettings.MiniPlayerSizePreset): return current as string == "Classic" ? "Compact" : "Classic";
            case nameof(AppSettings.MiniPlayerArtworkProgressThickness): return 3.5;
            case nameof(AppSettings.DisabledTrackContextMenuActions): return new List<string> { TrackContextMenuActions.CopyPath };
            case nameof(AppSettings.DisabledMiniPlayerContextMenuActions): return new List<string> { MiniPlayerContextMenuActions.Pin };
        }

        Type type = property.PropertyType;
        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(int)) return (int)current! + 1;
        if (type == typeof(long)) return (long)current! + 1;
        if (type == typeof(double)) return (double)current! + 0.5;
        if (type == typeof(double?)) return ((double?)current ?? 0.0) + 1.5;
        if (type == typeof(string)) return "different-value";
        if (type == typeof(double[])) return ((double[])current!).Select(gain => gain + 1.0).ToArray();
        if (type == typeof(HotkeyBinding)) return new HotkeyBinding { Ctrl = true, Shift = true, Key = "Z" };
        if (type == typeof(List<string>)) return new List<string> { "different-item" };
        if (type == typeof(List<EqualizerPreset>)) return new List<EqualizerPreset> { new() { Name = "different" } };
        if (type == typeof(List<SavedPlaylistFolder>))
            return new List<SavedPlaylistFolder> { new() { DisplayName = "different", Tracks = { "different.mp3" } } };
        if (type == typeof(Dictionary<string, int>)) return new Dictionary<string, int> { ["different.mp3"] = 1 };

        throw new NotSupportedException($"Тест не умеет подбирать другое значение для {name} ({type.Name}): добавь случай в DifferentValue.");
    }

    private static object? DifferentValueOrNullableString(PropertyInfo property, AppSettings defaults)
    {
        if (property.PropertyType == typeof(string) || Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType)
        {
            if (property.Name is nameof(AppSettings.LastTrackPath) or nameof(AppSettings.StatsStartedAt) or nameof(AppSettings.SkippedUpdateVersion))
                return "different-value";
            if (property.Name == nameof(AppSettings.SavedPlaylist))
                return new List<string> { "different.mp3" };
            if (property.Name == nameof(AppSettings.ForwardCompatibleProperties))
                return null;
        }

        return DifferentValue(property, defaults);
    }

    [Fact]
    public void EveryExclusionIsARealProperty_AndNoPropertyIsListedTwice()
    {
        var names = AllSettingsProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new List<string>(), UserDataAndRuntimeState.Concat(NotTransferable).Where(name => !names.Contains(name)).ToList());
        Assert.Equal(new List<string>(), UserDataAndRuntimeState.Intersect(NotTransferable).ToList());
    }

    [Fact]
    public void ImportCopiesEverySettingExceptTheOnesListedAsNotTransferable()
    {
        var excluded = UserDataAndRuntimeState.Concat(NotTransferable).ToHashSet(StringComparer.Ordinal);
        var forgotten = new List<string>();
        var copiedButListedAsExcluded = new List<string>();

        foreach (PropertyInfo property in AllSettingsProperties())
        {
            if (property.Name == nameof(AppSettings.ForwardCompatibleProperties))
                continue;

            var defaults = new AppSettings();
            var source = new AppSettings();
            var live = new AppSettings();
            property.SetValue(source, DifferentValueOrNullableString(property, defaults));

            LumiProfileIO.Apply(source, live);

            bool copied = Json(property.GetValue(live)) != Json(property.GetValue(defaults));
            if (copied && excluded.Contains(property.Name))
                copiedButListedAsExcluded.Add(property.Name);
            if (!copied && !excluded.Contains(property.Name))
                forgotten.Add(property.Name);
        }

        Assert.Equal(new List<string>(), forgotten);
        Assert.Equal(new List<string>(), copiedButListedAsExcluded);
    }

    [Fact]
    public void ResetToDefaultsRestoresEverySettingThatImportCopies()
    {
        var excluded = UserDataAndRuntimeState.Concat(NotTransferable).ToHashSet(StringComparer.Ordinal);
        var notReset = new List<string>();

        foreach (PropertyInfo property in AllSettingsProperties().Where(property => !excluded.Contains(property.Name)))
        {
            var defaults = new AppSettings();
            var live = new AppSettings();
            property.SetValue(live, DifferentValueOrNullableString(property, defaults));

            LumiProfileIO.ResetToDefaults(live);

            if (!SameValue(property.GetValue(live), property.GetValue(defaults)))
                notReset.Add(property.Name);
        }

        Assert.Equal(new List<string>(), notReset);
    }

    [Fact]
    public void ExportClearsEveryUserDataProperty()
    {
        var live = new AppSettings();
        foreach (PropertyInfo property in AllSettingsProperties().Where(property => UserDataAndRuntimeState.Contains(property.Name)))
            property.SetValue(live, DifferentValueOrNullableString(property, new AppSettings()));

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumisense-coverage-" + Guid.NewGuid().ToString("N") + LumiProfileIO.FileExtension);
        try
        {
            LumiProfileIO.Export(path, live);
            LumiProfile? profile = LumiProfileIO.TryReadFile(path);

            Assert.NotNull(profile);
            var defaults = new AppSettings();
            var leaked = AllSettingsProperties()
                .Where(property => UserDataAndRuntimeState.Contains(property.Name))
                .Where(property => property.Name != nameof(AppSettings.ShuffleHistoryIndex))
                .Where(property => Json(property.GetValue(profile!.Settings)) != Json(property.GetValue(defaults)))
                .Select(property => property.Name)
                .ToList();
            Assert.Equal(new List<string>(), leaked);
            Assert.Equal(-1, profile!.Settings.ShuffleHistoryIndex);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Apply_IgnoresUnknownIconPackAndOutOfRangeValuesFromOtherVersions()
    {
        var live = new AppSettings();
        var imported = new AppSettings
        {
            IconPack = "FuturePack",
            AppIcon = "FutureIcon",
            WasapiMode = "Turbo",
            TrackChangeToastArtSide = "Top",
            TrackChangeToastTextAlignment = "Justify",
            LyricsTextAlignment = "Justify",
            FavoriteHeartAnimation = "Justify",
            MiniPlayerSizePreset = "Justify",
            MiniPlayerArtworkProgressThickness = 99
        };

        LumiProfileIO.Apply(imported, live);

        var defaults = new AppSettings();
        Assert.Equal(defaults.IconPack, live.IconPack);
        Assert.Equal(defaults.AppIcon, live.AppIcon);
        Assert.Equal(defaults.WasapiMode, live.WasapiMode);
        Assert.Equal(defaults.TrackChangeToastArtSide, live.TrackChangeToastArtSide);
        Assert.Equal(defaults.TrackChangeToastTextAlignment, live.TrackChangeToastTextAlignment);
        Assert.Equal(defaults.LyricsTextAlignment, live.LyricsTextAlignment);
        Assert.Equal(defaults.FavoriteHeartAnimation, live.FavoriteHeartAnimation);
        Assert.Equal(defaults.MiniPlayerSizePreset, live.MiniPlayerSizePreset);
        Assert.Equal(4.0, live.MiniPlayerArtworkProgressThickness);
    }

    [Fact]
    public void Apply_NormalizesDisabledMiniPlayerMenuActions()
    {
        var live = new AppSettings();
        var imported = new AppSettings
        {
            DisabledMiniPlayerContextMenuActions = new List<string> { "bogus", MiniPlayerContextMenuActions.Pin, MiniPlayerContextMenuActions.Pin }
        };

        LumiProfileIO.Apply(imported, live);

        Assert.Equal(new[] { MiniPlayerContextMenuActions.Pin }, live.DisabledMiniPlayerContextMenuActions);
    }
}
