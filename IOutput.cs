namespace PlaytimeWatcher.Core;

// Любой способ показать/сохранить текущие цифры аккаунта: Discord-вебхук,
// консоль, файл, локальный сайт, что угодно ещё. Ядро вызывает все
// зарегистрированные выводы одинаково, ничего не зная про их реализацию.
public interface IOutput
{
    Task PublishAsync(SessionKey key, AccountState state);
}
