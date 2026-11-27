using System;
using System.Linq;

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

    // Форм-факторы PKEY_AudioEndpoint_FormFactor (EndpointFormFactor).
    private const int FormFactorSpeakers = 1;
    private const int FormFactorHeadphones = 3;
    private const int FormFactorHeadset = 5;
    private const int FormFactorUnknownDigital = 7;
    private const int FormFactorSpdif = 8;
    private const int FormFactorDisplay = 9;

    // PKEY_AudioEndpoint_JackSubType: KSNODETYPE_HEADPHONES и KSNODETYPE_HEAD_MOUNTED_DISPLAY_AUDIO / KSNODETYPE_SPEAKER.
    private static readonly string[] HeadphoneJackTypes = ["dff21ce2-f70f-11d0-b917-00a0c9223196", "dff21ce3-f70f-11d0-b917-00a0c9223196"];
    private static readonly string[] SpeakerJackTypes = ["dff21ce1-f70f-11d0-b917-00a0c9223196", "dff21ce4-f70f-11d0-b917-00a0c9223196"];

    // Свойства конечной точки точнее имени: гнездо с наушниками у многих драйверов называется «Динамики», но тип гнезда у него «наушники».
    public static AudioOutputKind Classify(string? friendlyName, int? formFactor, string? jackSubType)
    {
        AudioOutputKind byName = Classify(friendlyName);
        if (byName == AudioOutputKind.Bluetooth) return byName;

        if (formFactor == FormFactorDisplay) return AudioOutputKind.Display;
        if (formFactor is FormFactorSpdif or FormFactorUnknownDigital) return AudioOutputKind.Digital;

        string jack = (jackSubType ?? "").Trim('{', '}', ' ').ToLowerInvariant();
        if (HeadphoneJackTypes.Contains(jack)) return AudioOutputKind.Headphones;
        if (formFactor is FormFactorHeadphones or FormFactorHeadset) return AudioOutputKind.Headphones;

        if (byName != AudioOutputKind.Unknown) return byName;
        if (formFactor == FormFactorSpeakers || SpeakerJackTypes.Contains(jack)) return AudioOutputKind.Speakers;
        return AudioOutputKind.Unknown;
    }

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
        AudioOutputKind.Digital => "IconDigitalOutput",
        _ => "IconAudioWave"
    };

    private static bool ContainsAny(string text, string[] marks)
    {
        foreach (string mark in marks)
            if (text.Contains(mark, StringComparison.Ordinal)) return true;
        return false;
    }
}
