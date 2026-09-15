using System.Text.Json;

namespace PlaytimeWatcher.Core;

// Надёжное основное хранилище — не зависит от интернета/Discord.
public class LocalJsonStatsStore : IStatsStore
{
    private readonly string _dir;

    public LocalJsonStatsStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(SessionKey key) => Path.Combine(_dir, $"{key.SourceId}_{key.AccountId}.json");

    public async Task<AccountState> LoadAsync(SessionKey key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            try
            {
                await using var fs = File.OpenRead(path);
                var state = await JsonSerializer.DeserializeAsync<AccountState>(fs);
                if (state is not null) return state;
            }
            catch { /* повреждённый файл — начинаем с чистого состояния */ }
        }
        return new AccountState();
    }

    public async Task SaveAsync(SessionKey key, AccountState state)
    {
        try
        {
            await using var fs = File.Create(PathFor(key));
            await JsonSerializer.SerializeAsync(fs, state, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { /* следующий Flush попробует снова */ }
    }
}
