using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlaytimeWatcher.Core;

namespace PlaytimeWatcher.Outputs;

// Один из ВОЗМОЖНЫХ выводов. Ядро (TimeTracker) ничего не знает про Discord —
// можно добавить рядом любые другие IOutput, не трогая счётчик.
//
// "Одно сообщение на аккаунт, независимо от количества запусков": id
// сообщения для каждого SessionKey хранится в файле рядом на диске (не в
// самом Discord), поэтому при следующем запуске программа точно знает,
// какое сообщение редактировать (PATCH), а не создаёт новое.
public class DiscordWebhookOutput : IOutput
{
    private readonly HttpClient _http = new();
    private readonly string _webhookId;
    private readonly string _webhookToken;
    private readonly string _msgIdDir;

    public DiscordWebhookOutput(string webhookUrl, string msgIdDir)
    {
        var match = Regex.Match(webhookUrl, @"webhooks/(\d+)/([\w-]+)");
        if (!match.Success)
            throw new ArgumentException("Некорректный WEBHOOK_URL (не похож на ссылку вебхука Discord)");

        _webhookId = match.Groups[1].Value;
        _webhookToken = match.Groups[2].Value;
        _msgIdDir = msgIdDir;
        Directory.CreateDirectory(_msgIdDir);
    }

    private string BaseUrl => $"https://discord.com/api/webhooks/{_webhookId}/{_webhookToken}";
    private string MsgIdPath(SessionKey key) => Path.Combine(_msgIdDir, $"{key.SourceId}_{key.AccountId}.msgid");

    public async Task PublishAsync(SessionKey key, AccountState state)
    {
        var embed = BuildEmbed(key, state);
        var payload = new { embeds = new[] { embed } };

        var idPath = MsgIdPath(key);
        var existingId = File.Exists(idPath) ? await File.ReadAllTextAsync(idPath) : null;

        if (existingId is not null)
        {
            var patchResp = await _http.PatchAsync($"{BaseUrl}/messages/{existingId}", JsonContent.Create(payload));
            if (patchResp.IsSuccessStatusCode) return;
            // сообщение удалили вручную в Discord — создадим новое ниже
        }

        var postResp = await _http.PostAsJsonAsync($"{BaseUrl}?wait=true", payload);
        if (postResp.IsSuccessStatusCode)
        {
            var body = await postResp.Content.ReadFromJsonAsync<JsonElement>();
            var newId = body.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (newId is not null)
                await File.WriteAllTextAsync(idPath, newId);
        }
    }

    private static object BuildEmbed(SessionKey key, AccountState state)
    {
        var fields = new List<object>
        {
            new { name = "Всего времени", value = Format(state.TotalSeconds), inline = false }
        };

        foreach (var (activity, seconds) in state.Activities)
            fields.Add(new { name = activity, value = Format(seconds), inline = true });

        var name = state.DisplayName ?? key.AccountId;

        return new
        {
            title = $"{name} ({key.SourceId})",
            description = $"Источник: {key.SourceId}",
            color = 0x5865F2,
            fields
        };
    }

    private static string Format(double seconds) => $"{(int)(seconds / 60)} мин";
}
