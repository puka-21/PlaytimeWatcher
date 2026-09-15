namespace PlaytimeWatcher.Core;

// Это и есть "счётчик" сам по себе. Он ничего не знает ни про Roblox, ни про
// Discord, ни про какое-либо конкретное приложение — только про абстрактные
// SessionKey (кто) и строковые "активности" (что делает). Одновременно может
// вестись сколько угодно независимых сессий (разные окна Roblox, разные
// аккаунты, в будущем — другие приложения) — состояние каждой хранится
// отдельно и не мешает остальным.
public class TimeTracker
{
    private readonly IStatsStore _store;
    private readonly List<IOutput> _outputs;

    private readonly Dictionary<SessionKey, AccountState> _stats = new();
    private readonly Dictionary<SessionKey, (string Activity, DateTime Since)> _active = new();
    private readonly HashSet<SessionKey> _dirty = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TimeTracker(IStatsStore store, IEnumerable<IOutput> outputs)
    {
        _store = store;
        _outputs = outputs.ToList();
    }

    private async Task<AccountState> GetStateAsync(SessionKey key)
    {
        if (!_stats.TryGetValue(key, out var state))
        {
            state = await _store.LoadAsync(key);
            _stats[key] = state;
        }
        return state;
    }

    private void CommitElapsed(SessionKey key, AccountState state)
    {
        if (!_active.TryGetValue(key, out var active)) return;

        var elapsed = DateTime.UtcNow - active.Since;
        _active[key] = (active.Activity, DateTime.UtcNow);
        if (elapsed <= TimeSpan.Zero) return;

        state.TotalSeconds += elapsed.TotalSeconds;
        state.Activities.TryGetValue(active.Activity, out var existing);
        state.Activities[active.Activity] = existing + elapsed.TotalSeconds;
        _dirty.Add(key);
    }

    /// Провайдер сообщает: "для этого ключа сейчас идёт вот такая активность".
    /// Если ключ/активность поменялись — предыдущий отрезок фиксируется.
    public async Task SetActivityAsync(SessionKey key, string activity, string? displayName = null)
    {
        await _lock.WaitAsync();
        try
        {
            var state = await GetStateAsync(key);

            if (displayName is not null && state.DisplayName != displayName)
            {
                state.DisplayName = displayName;
                _dirty.Add(key);
            }

            if (_active.TryGetValue(key, out var current) && current.Activity == activity)
                return; // ничего не поменялось

            CommitElapsed(key, state);
            _active[key] = (activity, DateTime.UtcNow);
        }
        finally { _lock.Release(); }
    }

    /// Провайдер сообщает: "эта сессия закончилась" (окно/аккаунт/приложение
    /// закрылось) — фиксирует остаток времени и убирает из активных.
    public async Task EndSessionAsync(SessionKey key)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_stats.TryGetValue(key, out var state)) return;
            CommitElapsed(key, state);
            _active.Remove(key);
        }
        finally { _lock.Release(); }
    }

    /// Периодический тик: фиксирует накопленное время для ВСЕХ активных
    /// сессий сразу (чтобы не терять данные при аварийном завершении),
    /// сохраняет и публикует всё, что изменилось с прошлого раза.
    public async Task TickAndFlushAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var key in _active.Keys.ToList())
                CommitElapsed(key, _stats[key]);

            foreach (var key in _dirty.ToList())
            {
                var state = _stats[key];
                await _store.SaveAsync(key, state);

                foreach (var output in _outputs)
                {
                    try { await output.PublishAsync(key, state); }
                    catch { /* сбой одного вывода не должен ронять остальные */ }
                }
            }
            _dirty.Clear();
        }
        finally { _lock.Release(); }
    }
}
