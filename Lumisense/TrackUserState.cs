namespace Lumisense;

// Состояние, которое видит пользователь рядом с текущим треком. Это не копия низкоуровневого
// NAudio PlaybackState: Loading и Error нужны именно для понятного сценария интерфейса.
//
// Это не проекция MainWindow._isPlaying: во время fade-out при смене трека Loading и _isPlaying == true
// действительны одновременно (см. LoadAndPlayAsync) — сливать их в одно поле нельзя.
internal enum TrackUserState
{
    NoTrack,
    Loading,
    Playing,
    Paused,
    Stopped,
    Error
}

// Причина готовности нового трека — позволяет политике уведомлений отличать явный выбор
// пользователя от автоперехода, восстановления сессии или перезагрузки после правки тега.
internal enum TrackChangeOrigin
{
    User,
    Automatic,
    SessionRestore,
    ExternalEdit
}
