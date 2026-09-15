namespace PlaytimeWatcher.Core;

// Идентифицирует "кого считаем": источник (например "roblox", в будущем
// "steam", "vscode" и т.д.) + идентификатор аккаунта внутри этого источника.
// Ядро (TimeTracker) больше ни о чём не спрашивает — ему всё равно, что за
// источник, лишь бы ключ был стабильным.
public readonly record struct SessionKey(string SourceId, string AccountId)
{
    public override string ToString() => $"{SourceId}:{AccountId}";
}
