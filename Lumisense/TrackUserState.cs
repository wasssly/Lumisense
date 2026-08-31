namespace Lumisense;

// Состояние, которое видит пользователь рядом с текущим треком. Это не копия низкоуровневого
// NAudio PlaybackState: Loading и Error нужны именно для понятного сценария интерфейса.
internal enum TrackUserState
{
    NoTrack,
    Loading,
    Playing,
    Paused,
    Stopped,
    Error
}

// Причина готовности нового трека. Позволяет политике уведомлений отличать явный выбор
// пользователя от естественного перехода, восстановления сессии или перезагрузки после
// внешнего редактирования тега/обложки.
internal enum TrackChangeOrigin
{
    User,
    Automatic,
    SessionRestore,
    ExternalEdit
}
