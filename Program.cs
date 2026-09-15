// Program.cs — точка входа. Только "склеивает" части вместе:
//   Core       — счётчик времени сам по себе, ничего не знает про Roblox/Discord.
//   Outputs    — куда показывать цифры (Discord — один из вариантов).
//   Providers  — за чем следим (Roblox — первое "приложение", дальше можно добавлять свои).
//
// Настройка в Bloxstrap: Расположение приложения -> путь к .exe,
// Аргументы запуска -> пусто, "Автоматически закрывать при закрытии Roblox" -> да.
//
// Сборка: dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

using PlaytimeWatcher.Core;
using PlaytimeWatcher.Outputs;
using PlaytimeWatcher.Providers;

// ==================== Не даём запустить второй экземпляр ====================
using var singleInstanceMutex = new Mutex(true, "Global\\PlaytimeWatcher_SingleInstance", out var isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine("PlaytimeWatcher уже запущен в другом процессе — выхожу.");
    return;
}

// ==================== НАСТРОЙКИ ====================
const string WebhookUrl = "https://discord.com/api/webhooks/1548451723838885890/0_4f5b2jv2Jwtc-XXMjVWt2gcVGevw3EO7Eqi5v-RHScCqJEcHBfpaj_hG7PMNt7egsR";
var flushInterval = TimeSpan.FromSeconds(60);
// =====================================================

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "PlaytimeWatcher");
Directory.CreateDirectory(dataDir);

// ---- Хранилище: ядро не знает и не должно знать, откуда пришли данные ----
var store = new LocalJsonStatsStore(Path.Combine(dataDir, "stats"));

// ---- Выводы: их может быть сколько угодно, Discord ничем не привилегирован ----
var outputs = new List<IOutput>();
try
{
    outputs.Add(new DiscordWebhookOutput(WebhookUrl, Path.Combine(dataDir, "discord_messages")));
    Console.WriteLine("Discord-вывод подключён.");
}
catch (Exception ex)
{
    Console.WriteLine($"Discord-вывод отключён ({ex.Message}) — программа продолжит работать без него.");
}
outputs.Add(new ConsoleOutput());

var tracker = new TimeTracker(store, outputs);

// ---- Провайдеры: "приложения", за которыми следим, каждый — своя параллельная задача ----
var providers = new List<IProvider>
{
    new RobloxProvider(),
    new SteamProvider(),
    // Чтобы добавить слежение за другим приложением в будущем — реализуй
    // IProvider и добавь сюда ещё один экземпляр. Ядро и остальные
    // провайдеры трогать не нужно.
};

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.TickAndFlushAsync().GetAwaiter().GetResult();

var providerTasks = providers.Select(p => p.RunAsync(tracker, cts.Token)).ToList();

var flushTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await Task.Delay(flushInterval, cts.Token); }
        catch (TaskCanceledException) { break; }
        await tracker.TickAndFlushAsync();
    }
});

Console.WriteLine("PlaytimeWatcher запущен. Провайдеры: " + string.Join(", ", providers.Select(p => p.GetType().Name)));

await Task.WhenAll(providerTasks.Append(flushTask));
await tracker.TickAndFlushAsync();
