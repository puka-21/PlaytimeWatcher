namespace PlaytimeWatcher.Core;

public interface IStatsStore
{
    Task<AccountState> LoadAsync(SessionKey key);
    Task SaveAsync(SessionKey key, AccountState state);
}
