using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamDB.Infrastructure.Data;
using SteamDB.Infrastructure.Steam;

namespace SteamDB.Infrastructure;

public static class SteamDbServices
{
    public static IServiceCollection AddSteamAnalytics(this IServiceCollection services, string contentRoot)
    {
        var data = DataDirectory.Resolve(contentRoot);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        Directory.CreateDirectory(Path.Combine(data, "keys"));
        var database = Path.Combine(data, "steamdb.sqlite");
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={database}"));
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
        services.AddHttpClient("steam", client => client.Timeout = TimeSpan.FromSeconds(40));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("steam");
            return new PoliteSteamClient(http, sp.GetRequiredService<ISettingsStore>());
        });
        services.AddSingleton<IDatasetCache, DatasetCache>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<ITableExport, TableExport>();
        services.AddSingleton<IMyGames, MyGames>();
        services.AddSingleton<IIdeaSearch, IdeaSearch>();
        services.AddSingleton<ICollectionBoardSource, CollectionBoardSource>();
        services.AddSingleton<CollectionCoordinator>();
        services.AddSingleton<ICollectionController>(sp => sp.GetRequiredService<CollectionCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<CollectionCoordinator>());
        services.AddScoped<CollectionRunner>();
        services.AddSingleton<ILoggerProvider>(_ => new FileLogProvider(Path.Combine(data, "logs", "steamdb.log")));
        return services;
    }

    public static async Task PrepareSteamDbAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await services.GetRequiredService<SettingsStore>().LoadAsync(ct);
        await services.GetRequiredService<IDatasetCache>().ReloadAsync(ct);
    }
}

public sealed class FileLogProvider(string path) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLog> _loggers = new();

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLog(path, name));

    public void Dispose()
    {
    }

    private sealed class FileLog(string path, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var text = formatter(state, exception);
            if (text.Contains("x-webapi-key", StringComparison.OrdinalIgnoreCase) || text.Contains("key=", StringComparison.OrdinalIgnoreCase))
                return;
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {logLevel} {category}: {text}{Environment.NewLine}";
            try
            {
                lock (FileLogProvider.Gate)
                    File.AppendAllText(path, line);
            }
            catch (IOException)
            {
            }
        }
    }

    private static readonly object Gate = new();
}
