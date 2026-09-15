using System.Text.Json.Serialization;

namespace PlaytimeWatcher.Core;

public class AccountState
{
    // Ник/логин, если провайдер смог его получить. Если нет — используем
    // AccountId из SessionKey как есть.
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("total")]
    public double TotalSeconds { get; set; }

    // Произвольные "активности" — для Roblox это названия игр, но провайдер
    // может класть сюда что угодно (режим, сцену, документ и т.п.).
    [JsonPropertyName("activities")]
    public Dictionary<string, double> Activities { get; set; } = new();
}
