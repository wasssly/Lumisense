using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Lumisense;

// Переписывает значок ярлыков Lumisense (рабочий стол и меню «Пуск») под выбранную в настройках иконку.
// Без этого окна и трей меняются сразу, а ярлыки остаются со значком, заданным при установке.
// Закреплённые на панели задач ярлыки не трогаем: у них свой AppUserModelID и хранилище свойств.
internal static class ShortcutIconSync
{
    private const string ShortcutExeName = "Lumisense.exe";
    private const int MaxPath = 1024;

    // Синхронизация нужна при смене иконки и на старте, если выбрана не иконка сборки (Aurora):
    // установщик и обновление Velopack пересоздают ярлыки со значком Aurora.
    public static Task SyncAsync(string icon)
    {
        if (!AppIcons.IsKnown(icon)) return Task.CompletedTask;

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return Task.CompletedTask;

        // Ресурс читаем здесь, в потоке вызывающего: pack-URI привязан к приложению WPF.
        byte[] iconBytes;
        try
        {
            var resource = Application.GetResourceStream(AppIcons.GetUri(icon));
            if (resource is null) return Task.CompletedTask;
            using var stream = resource.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            iconBytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn($"ShortcutIconSync: не удалось прочитать ресурс значка {icon}: {ex.Message}");
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        // Ярлыки — COM-объекты, поэтому отдельный STA-поток, а не пул.
        var thread = new Thread(() =>
        {
            try
            {
                Sync(AppIcons.GetFileName(icon), iconBytes, exePath);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShortcutIconSync",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Sync(string iconFileName, byte[] iconBytes, string exePath)
    {
        string iconPath = EnsureIconFile(iconFileName, iconBytes);
        string exeDirectory = Path.GetDirectoryName(exePath) ?? "";
        string? parentDirectory = Path.GetDirectoryName(exeDirectory);

        int updated = 0;
        foreach (string shortcut in EnumerateShortcuts())
        {
            try
            {
                if (UpdateShortcut(shortcut, iconPath, exeDirectory, parentDirectory)) updated++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or COMException)
            {
                // Ярлыки общего рабочего стола и меню «Пуск» без прав администратора не записываются.
                Logger.Info($"ShortcutIconSync: ярлык пропущен ({Path.GetFileName(shortcut)}): {ex.Message}");
            }
        }

        if (updated > 0)
        {
            // Просим проводник перечитать значки, иначе старый останется в кэше до перезапуска.
            SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
            Logger.Info($"ShortcutIconSync: обновлён значок у ярлыков: {updated}.");
        }
    }

    // Velopack удаляет и пересоздаёт папку приложения при обновлении, поэтому копию значка
    // держим в LocalAppData, а не рядом с exe.
    private static string EnsureIconFile(string fileName, byte[] iconBytes)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumisense", "icons");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName + ".ico");
        if (!File.Exists(path) || new FileInfo(path).Length != iconBytes.Length)
            File.WriteAllBytes(path, iconBytes);
        return path;
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var topOnly = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (string directory in topOnly)
        {
            foreach (string file in EnumerateFiles(directory, SearchOption.TopDirectoryOnly))
                yield return file;
        }

        var recursive = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        foreach (string directory in recursive)
        {
            foreach (string file in EnumerateFiles(directory, SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static List<string> EnumerateFiles(string directory, SearchOption option)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return result;
        try
        {
            // Имя файла ярлыка может быть любым (его меняет пользователь), поэтому смотрим на цель.
            result.AddRange(Directory.EnumerateFiles(directory, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 3,
            }));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Logger.Info($"ShortcutIconSync: папка недоступна ({directory}): {ex.Message}");
        }
        return result;
    }

    private static bool UpdateShortcut(string shortcutPath, string iconPath, string exeDirectory, string? parentDirectory)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);

            var target = new StringBuilder(MaxPath);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawPath);
            string targetPath = target.ToString();
            if (!string.Equals(Path.GetFileName(targetPath), ShortcutExeName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Velopack кладёт ярлык на заглушку в корне установки, Inno — на exe в папке приложения.
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            bool ours = string.Equals(targetDirectory, exeDirectory, StringComparison.OrdinalIgnoreCase)
                || (parentDirectory is not null
                    && string.Equals(targetDirectory, parentDirectory, StringComparison.OrdinalIgnoreCase));
            if (!ours) return false;

            var current = new StringBuilder(MaxPath);
            link.GetIconLocation(current, current.Capacity, out int currentIndex);
            if (currentIndex == 0 && string.Equals(current.ToString(), iconPath, StringComparison.OrdinalIgnoreCase))
                return false;

            link.SetIconLocation(iconPath, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private const int SlgpRawPath = 0x4;
    private const int ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
