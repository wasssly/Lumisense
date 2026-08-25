using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioPlayer;

// Очередь "Играть следующим" — временная вставка треков перед обычным продолжением
// плейлиста/шаффла (см. MainWindow.ResolveNextTrackPathRespectingQueue). Не меняет сам
// плейлист/папки; после того как очередь опустеет, воспроизведение продолжается ровно с
// того места, где было бы без неё.
public sealed class PlaybackQueue
{
    private readonly List<string> _items = new();

    public IReadOnlyList<string> Items => _items;
    public int Count => _items.Count;

    public event Action? Changed;

    // Порядок paths сохраняется как есть в начале очереди (InsertRange, а не Insert по
    // одному, что дало бы обратный порядок).
    public void PlayNext(IEnumerable<string> paths)
    {
        var list = paths as IList<string> ?? paths.ToList();
        if (list.Count == 0) return;

        _items.InsertRange(0, list);
        Changed?.Invoke();
    }

    public void AddToEnd(IEnumerable<string> paths)
    {
        var list = paths as IList<string> ?? paths.ToList();
        if (list.Count == 0) return;

        _items.AddRange(list);
        Changed?.Invoke();
    }

    public string? PeekNext() => _items.Count > 0 ? _items[0] : null;

    // Возвращает и убирает первый элемент — единственная точка реального "потребления"
    // очереди при воспроизведении (см. ResolveNextTrackPathRespectingQueue). Не используется
    // при быстрой прокрутке несколькими шагами подряд по зажатой клавише (см. комментарий
    // над CommitPendingHotkeyTrackStep) — это намеренно, чтобы прокрутка не съедала
    // элементы очереди, которые в итоге не будут воспроизведены.
    public string? PopNext()
    {
        if (_items.Count == 0) return null;

        string next = _items[0];
        _items.RemoveAt(0);
        Changed?.Invoke();
        return next;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        _items.RemoveAt(index);
        Changed?.Invoke();
    }

    // Убирает первое совпадение по точному пути — используется панелью очереди, где элементы
    // адресуются по FilePath, а не по индексу (индекс мог сдвинуться между рендером и кликом).
    public bool Remove(string path)
    {
        int index = _items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;

        _items.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;

        _items.Clear();
        Changed?.Invoke();
    }

    // Убирает записи, для которых файла больше нет на диске — вызывается при восстановлении
    // сохранённой очереди на старте, чтобы пропавшие за время простоя файлы не висели в
    // очереди как будто всё ещё доступны.
    public int PruneMissing()
    {
        int removed = _items.RemoveAll(path => !File.Exists(path));
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public void LoadFrom(IEnumerable<string> paths)
    {
        _items.Clear();
        _items.AddRange(paths);
        Changed?.Invoke();
    }
}
