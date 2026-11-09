using System;

namespace Lumisense;

public enum AudioOutputKind
{
    Unknown,
    Speakers,
    Headphones,
    Bluetooth,
    Display,
    Digital,
    Usb
}

// Данные об устройстве вывода для острова: Announce — выход сменился на другое устройство, NewlyConnected — оно только что подключено.
public readonly record struct AudioOutputDeviceInfo(string Name, AudioOutputKind Kind, bool Announce, bool NewlyConnected);

// Тип берётся из свойств конечной точки (jack, форм-фактор) и имени; признака Bluetooth в API нет, он ищется по имени.
public static class AudioOutputKindClassifier
{
    private static readonly string[] BluetoothMarks = ["bluetooth", "hands-free", "handsfree", "a2dp", "airpods", "buds"];
    private static readonly string[] DisplayMarks = ["hdmi", "displayport", "display audio", "nvidia high definition", "amd high definition", "монитор"];
    private static readonly string[] DigitalMarks = ["s/pdif", "spdif", "digital output", "optical", "цифров"];
    private static readonly string[] HeadphoneMarks = ["headphone", "headset", "earphone", "earbud", "наушник", "гарнитур", "головн"];
    private static readonly string[] SpeakerMarks = ["speaker", "динамик", "колонк"];

    public static AudioOutputKind Classify(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return AudioOutputKind.Unknown;

        string name = friendlyName.ToLowerInvariant();
        if (ContainsAny(name, BluetoothMarks)) return AudioOutputKind.Bluetooth;
        if (ContainsAny(name, DisplayMarks)) return AudioOutputKind.Display;
        if (ContainsAny(name, DigitalMarks)) return AudioOutputKind.Digital;
        if (name.Contains("usb", StringComparison.Ordinal)) return AudioOutputKind.Usb;
        if (ContainsAny(name, HeadphoneMarks)) return AudioOutputKind.Headphones;
        if (ContainsAny(name, SpeakerMarks)) return AudioOutputKind.Speakers;
        return AudioOutputKind.Unknown;
    }

    // Ключ SVG-иконки (см. SvgPathIcon): рисуется в активном паке, как остальные значки плеера.
    public static string IconKey(AudioOutputKind kind) => kind switch
    {
        AudioOutputKind.Headphones => "IconHeadphones",
        AudioOutputKind.Bluetooth => "IconBluetooth",
        AudioOutputKind.Speakers => "IconSpeakerHifi",
        AudioOutputKind.Display => "IconMonitor",
        AudioOutputKind.Usb => "IconUsb",
        _ => "IconAudioWave"
    };

    private static bool ContainsAny(string text, string[] marks)
    {
        foreach (string mark in marks)
            if (text.Contains(mark, StringComparison.Ordinal)) return true;
        return false;
    }
}
