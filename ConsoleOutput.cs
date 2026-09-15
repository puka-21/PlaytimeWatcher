using PlaytimeWatcher.Core;

namespace PlaytimeWatcher.Outputs;

// Просто печатает текущие цифры в консоль. Показывает, что выводов может
// быть сколько угодно одновременно — Discord тут ничем не привилегирован.
public class ConsoleOutput : IOutput
{
    public Task PublishAsync(SessionKey key, AccountState state)
    {
        var name = state.DisplayName ?? key.AccountId;
        Console.WriteLine($"[stats] {key.SourceId}/{name}: всего {(int)(state.TotalSeconds / 60)} мин");
        return Task.CompletedTask;
    }
}
