using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PlaytimeWatcher.Core;

namespace PlaytimeWatcher.Providers;

// Следит за Steam через локальный реестр и файлы клиента — никакого API-ключа
// и никакой авторизации не требуется, никакие пароли/токены не читаются.
//
// Игра: HKCU\Software\Valve\Steam\RunningAppID — Steam сам обновляет этот
// ключ на AppID запущенной сейчас игры (0, если игра не запущена). Название
// игры получаем через открытый Steam Store API (appdetails), без ключа.
//
// Аккаунт: HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser — 32-битный
// AccountID залогиненного сейчас в Steam-клиенте пользователя. Ник
// подтягивается из локального файла Steam\config\loginusers.vdf, который
// сам Steam хранит на диске в открытом текстовом формате.
//
// ОГОВОРКА: точные имена этих реестровых ключей я не могу проверить без
// реального Steam на этом ПК — они основаны на распространённой практике,
// но могут отличаться версия от версии. Debug-вывод покажет, что реально
// прочиталось — если там пусто/не то, пришли мне вывод, поправлю.
//
// ОГРАНИЧЕНИЕ (не моё, а самого Steam): оба реестровых ключа общие на весь
// Windows-профиль. Steam физически не позволяет одному Windows-пользователю
// залогинить два аккаунта и запустить две игры одновременно — значит
// параллельность здесь сработает только если у тебя несколько ИЗОЛИРОВАННЫХ
// установок Steam (например, разные Windows-профили на одном ПК).
public class SteamProvider : IProvider
{
    private const string SteamRegPath = @"Software\Valve\Steam";
    private const string ActiveUserRegPath = @"Software\Valve\Steam\ActiveProcess";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http = new();
    private readonly Dictionary<long, string> _gameNameCache = new();
    private readonly Dictionary<long, string> _personaNameCache = new();

    private long? _lastAccountId;
    private long _lastAppId;

    public async Task RunAsync(TimeTracker tracker, CancellationToken ct)
    {
        Console.WriteLine("[steam] провайдер запущен, слежу за реестром Steam...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(tracker);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[steam] ошибка: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct); } catch (TaskCanceledException) { }
        }
    }

    private async Task PollAsync(TimeTracker tracker)
    {
        var accountId = ReadActiveUser();
        var appId = ReadRunningAppId();

        if (accountId != _lastAccountId || appId != _lastAppId)
            Console.WriteLine($"[debug:steam] ActiveUser={accountId?.ToString() ?? "null"} RunningAppID={appId}");

        if (accountId is null || accountId == 0)
        {
            // никто не залогинен в Steam-клиенте — закрываем то, что шло раньше
            if (_lastAccountId is long prevAcc && prevAcc != 0)
                await tracker.EndSessionAsync(new SessionKey("steam", ToSteamId64(prevAcc).ToString()));

            _lastAccountId = null;
            _lastAppId = 0;
            return;
        }

        var uid = ToSteamId64(accountId.Value);
        var key = new SessionKey("steam", uid.ToString());

        // сменился аккаунт — закрываем предыдущую сессию перед стартом новой
        if (_lastAccountId is long prevAccountId && prevAccountId != accountId)
        {
            var prevKey = new SessionKey("steam", ToSteamId64(prevAccountId).ToString());
            await tracker.EndSessionAsync(prevKey);
        }

        if (appId == 0)
        {
            // считаем только время В играх — нет игры, значит сессия закрыта
            if (_lastAppId != 0)
                await tracker.EndSessionAsync(key);
        }
        else if (appId != _lastAppId || accountId != _lastAccountId)
        {
            var gameName = await ResolveGameNameAsync(appId);
            var personaName = ResolvePersonaName(uid);
            await tracker.SetActivityAsync(key, gameName, personaName);
        }

        _lastAccountId = accountId;
        _lastAppId = appId;
    }

    private static long? ReadActiveUser()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ActiveUserRegPath);
        return key?.GetValue("ActiveUser") is int i ? i : null;
    }

    private static long ReadRunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamRegPath);
        return key?.GetValue("RunningAppID") is int i ? i : 0;
    }

    private static long ToSteamId64(long accountId) => 76561197960265728L + accountId;

    private async Task<string> ResolveGameNameAsync(long appId)
    {
        if (_gameNameCache.TryGetValue(appId, out var cached)) return cached;

        string name = $"App {appId}";
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>(
                $"https://store.steampowered.com/api/appdetails?appids={appId}");
            if (resp.TryGetProperty(appId.ToString(), out var appNode) &&
                appNode.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameEl))
            {
                name = nameEl.GetString() ?? name;
            }
        }
        catch { /* офлайн / API недоступно — останется "App <id>" */ }

        _gameNameCache[appId] = name;
        return name;
    }

    private string ResolvePersonaName(long steamId64)
    {
        if (_personaNameCache.TryGetValue(steamId64, out var cached)) return cached;

        var name = steamId64.ToString();
        try
        {
            var steamPath = FindSteamInstallPath();
            if (steamPath is not null)
            {
                var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (File.Exists(vdfPath))
                {
                    var found = ExtractPersonaName(File.ReadAllLines(vdfPath), steamId64.ToString());
                    if (found is not null) name = found;
                }
            }
        }
        catch { /* не критично — останется просто SteamID */ }

        _personaNameCache[steamId64] = name;
        return name;
    }

    // Простой построчный разбор блока loginusers.vdf вида:
    //   "76561198000000000"
    //   {
    //       "AccountName"   "..."
    //       "PersonaName"   "Имя"
    //       ...
    //   }
    private static string? ExtractPersonaName(string[] lines, string steamId64)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains($"\"{steamId64}\"")) continue;

            int depth = 0;
            for (int j = i; j < lines.Length; j++)
            {
                var line = lines[j];
                if (line.Contains('{')) depth++;
                if (line.Contains('}'))
                {
                    depth--;
                    if (depth <= 0) break;
                }

                var m = Regex.Match(line, "\"PersonaName\"\\s*\"([^\"]*)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        return null;
    }

    private static string? FindSteamInstallPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamRegPath);
        return key?.GetValue("SteamPath") as string;
    }
}
