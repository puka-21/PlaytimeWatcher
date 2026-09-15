using PlaytimeWatcher.Core;

namespace PlaytimeWatcher.Providers;

// "Приложение", за которым следим (Roblox — первое из них). Ядро (TimeTracker)
// про конкретные провайдеры не знает вообще ничего — просто запускает
// RunAsync и ждёт, пока провайдер сам вызывает SetActivityAsync/EndSessionAsync.
// Чтобы добавить слежение за новым приложением в будущем — достаточно
// реализовать этот интерфейс, ядро и остальные провайдеры трогать не нужно.
public interface IProvider
{
    Task RunAsync(TimeTracker tracker, CancellationToken ct);
}
