using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlaytimeWatcher.Core;

namespace PlaytimeWatcher.Providers;

// Следит сразу за НЕСКОЛЬКИМИ одновременно открытыми окнами Roblox (в т.ч.
// разных аккаунтов), потому что каждое окно Roblox пишет СВОЙ отдельный
// лог-файл в %LocalAppData%\Roblox\logs — мы просто тейлим их все параллельно,
// у каждого файла своё независимое состояние (какой аккаунт, какая игра).
//
// Аккаунт определяется по числовому UserId, который встречается в тексте
// лога (это публичный ID, не токен/cookie — Credential Manager не читается).
// Игра определяется по placeId из строки "Joining game ... place <id>".
// И то и другое превращается в человекочитаемое имя (ник, название игры)
// через ОТКРЫТОЕ Roblox API, без авторизации.
//
// Важная оговорка: я не могу проверить точный формат строк лога без доступа
// к реальному файлу — регулярки написаны по разумному предположению.
// Каждая подходящая строка печатается в консоль с префиксом [debug:*],
// чтобы можно было сверить/поправить паттерн.
public class RobloxProvider : IProvider
{
    private static readonly Regex JoinRegex = new(@"Joining game '[^']*' place (\d+)", RegexOptions.Compiled);
    private static readonly Regex UserIdRegex = new(@"userid[^\d]{0,5}(\d{4,15})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Если лог-файл не обновлялся дольше этого — считаем, что окно закрыто.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http = new();
    private readonly Dictionary<long, string> _gameNameCache = new();
    private readonly Dictionary<long, string> _userNameCache = new();
    private readonly Dictionary<string, FileState> _files = new(); // путь к логу -> состояние окна

    private class FileState
    {
        public long Position;
        public long? PlaceId;
        public long? UserId;
        public DateTime LastActivity = DateTime.UtcNow;
    }

    public async Task RunAsync(TimeTracker tracker, CancellationToken ct)
    {
        Console.WriteLine("[roblox] провайдер запущен, слежу за окнами Roblox...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Roblox", "logs");

                if (Directory.Exists(logsDir))
                {
                    var logFiles = Directory.GetFiles(logsDir, "*.log");

                    // Обрабатываем все файлы параллельно — это и есть
                    // поддержка нескольких одновременных окон/аккаунтов.
                    await Task.WhenAll(logFiles.Select(path => ProcessFileAsync(path, tracker)));

                    await CleanUpClosedWindowsAsync(logFiles, tracker);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[roblox] ошибка в цикле: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (TaskCanceledException) { }
        }
    }

    private async Task ProcessFileAsync(string path, TimeTracker tracker)
    {
        if (!_files.TryGetValue(path, out var state))
        {
            state = new FileState();
            // Свежий файл (недавно создан) — читаем с начала, чтобы не
            // пропустить вход в игру. Старый — начинаем с конца, чтобы не
            // парсить старый текст заново.
            var info = new FileInfo(path);
            var isFresh = (DateTime.UtcNow - info.CreationTimeUtc) < TimeSpan.FromMinutes(5);
            state.Position = isFresh ? 0 : info.Length;
            _files[path] = state;
        }

        List<string> newLines = new();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < state.Position) state.Position = 0; // лог пересоздан

            fs.Seek(state.Position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
                newLines.Add(line);

            state.Position = fs.Position;
        }
        catch (IOException)
        {
            return; // файл временно занят — попробуем на следующем тике
        }

        if (newLines.Count == 0) return;
        state.LastActivity = DateTime.UtcNow;

        foreach (var line in newLines)
            await HandleLineAsync(path, line, state, tracker);
    }

    private async Task HandleLineAsync(string path, string line, FileState state, TimeTracker tracker)
    {
        if (line.Contains("Joining", StringComparison.OrdinalIgnoreCase) && line.Contains("place", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[debug:join] {Path.GetFileName(path)}: {line}");
        if (line.Contains("userid", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"[debug:userid] {Path.GetFileName(path)}: {line}");

        long? detectedUserId = null;
        var userMatch = UserIdRegex.Match(line);
        if (userMatch.Success && long.TryParse(userMatch.Groups[1].Value, out var uid))
            detectedUserId = uid;

        var joinMatch = JoinRegex.Match(line);
        if (joinMatch.Success && long.TryParse(joinMatch.Groups[1].Value, out var placeId))
            state.PlaceId = placeId;

        // ВАЖНО: если UserId для этого лог-файла поменялся — сначала закрываем
        // старую сессию, иначе старый и новый аккаунт будут тикать ПАРАЛЛЕЛЬНО
        // и накручивать одинаковое время (это и был баг с дублированием минут).
        if (detectedUserId is long newUserId && newUserId != state.UserId)
        {
            if (state.UserId is long oldUserId)
            {
                var oldKey = new SessionKey("roblox", oldUserId.ToString());
                await tracker.EndSessionAsync(oldKey);
                Console.WriteLine($"[roblox] UserId для {Path.GetFileName(path)} сменился: {oldUserId} -> {newUserId}");
            }
            state.UserId = newUserId;
        }

        if (state.UserId is null || state.PlaceId is null) return;

        var key = new SessionKey("roblox", state.UserId.Value.ToString());
        var gameName = await ResolveGameNameAsync(state.PlaceId.Value);
        var displayName = await ResolveUserNameAsync(state.UserId.Value);

        await tracker.SetActivityAsync(key, gameName, displayName);
    }

    private async Task CleanUpClosedWindowsAsync(string[] currentFiles, TimeTracker tracker)
    {
        var currentSet = new HashSet<string>(currentFiles);
        foreach (var path in _files.Keys.ToList())
        {
            var state = _files[path];
            var goneOrIdle = !currentSet.Contains(path) || (DateTime.UtcNow - state.LastActivity) > IdleTimeout;
            if (!goneOrIdle) continue;

            if (state.UserId is long uid)
            {
                var key = new SessionKey("roblox", uid.ToString());
                await tracker.EndSessionAsync(key);
                Console.WriteLine($"[roblox] окно закрыто: {Path.GetFileName(path)} (UserId={uid})");
            }
            _files.Remove(path);
        }
    }

    private async Task<string> ResolveGameNameAsync(long placeId)
    {
        if (_gameNameCache.TryGetValue(placeId, out var cached)) return cached;

        string name = $"Place {placeId}";
        try
        {
            var universeResp = await _http.GetFromJsonAsync<JsonElement>(
                $"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
            var universeId = universeResp.GetProperty("universeId").GetInt64();

            var gamesResp = await _http.GetFromJsonAsync<JsonElement>(
                $"https://games.roblox.com/v1/games?universeIds={universeId}");
            var data = gamesResp.GetProperty("data");
            if (data.GetArrayLength() > 0)
                name = data[0].GetProperty("name").GetString() ?? name;
        }
        catch { /* офлайн / API недоступно — останется "Place <id>" */ }

        _gameNameCache[placeId] = name;
        return name;
    }

    private async Task<string> ResolveUserNameAsync(long userId)
    {
        if (_userNameCache.TryGetValue(userId, out var cached)) return cached;

        string name = userId.ToString();
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(
                $"https://users.roblox.com/v1/users/{userId}");
            name = resp.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? name : name;
        }
        catch { /* офлайн / API недоступно — останется просто числом */ }

        _userNameCache[userId] = name;
        return name;
    }
}
