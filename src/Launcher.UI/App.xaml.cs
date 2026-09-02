using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Launcher.Core.Abstractions;
using Launcher.Core.Services;
using Launcher.Core.Sources;
using Launcher.Core.ViewModels;
using Launcher.Engine.Butler;
using Launcher.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Launcher.UI;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    private static string LogPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface failures instead of writing them to a file nobody reads. The previous build
        // swallowed a startup exception into a log and showed an empty window with no explanation.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TryLogFatal(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLogFatal(args.Exception, "Task");
            args.SetObserved();
        };

        ServiceProvider = BuildServices();

        var logger = ServiceProvider.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Launcher starting. Log file: {Path}", LogPath);

        var window = ServiceProvider.GetRequiredService<MainWindow>();
        window.Show();

        // Initialise off the UI thread, but awaited so a failure is reported rather than lost.
        // The view model marshals every collection change back through IUiDispatcher, which is
        // what the previous build was missing when it did the same thing from Task.Run.
        var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();
        try
        {
            await Task.Run(() => viewModel.InitializeAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup initialisation failed.");
            MessageBox.Show(
                $"The launcher started but could not finish setting up:\n\n{ex.Message}\n\nDetails in {LogPath}",
                "Birdbox Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new FileLoggerProvider(LogPath, LogLevel.Information));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IConfigService, LocalJsonConfigService>();
        services.AddSingleton<IItchCatalogService, ItchProfileCatalogService>();

        // ---- content sources, in preference order ----

        // Self-hosted chunk CDN. Left unconfigured until the bucket exists; it reports
        // unavailable rather than failing, so wharf handles everything in the meantime.
        services.AddSingleton<IContentSource>(sp => new R2ChunkContentSource(
            baseUrl: Environment.GetEnvironmentVariable("LAUNCHER_CDN_BASE_URL"),
            http: null,
            logger: sp.GetService<ILogger<R2ChunkContentSource>>()));

        services.AddSingleton<IContentSource>(sp => new WharfContentSource(
            butlerPath: ButlerPath,
            dbPath: ButlerDbPath,
            apiKeyProvider: ReadItchApiKey,
            logger: sp.GetService<ILogger<WharfContentSource>>()));

        services.AddSingleton<IGameInstallEngine>(sp => new ContentSourceGameEngine(
            sp.GetServices<IContentSource>(),
            sp.GetService<ILogger<ContentSourceGameEngine>>()));

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static string ButlerPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "butler", "butler.exe");

    private static string ButlerDbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyGameLauncher", "butler.db");

    /// <summary>
    /// Finds an itch.io API key for butlerd's patch pipeline.
    ///
    /// Deliberately never embedded in the build: a key that can fetch builds must not ship in a
    /// launcher handed to players. It is read from the environment, or from the local butler login
    /// on a developer machine. Absent means no delta path, which the wharf source reports rather
    /// than failing — and is exactly why the R2 chunk source is the player-facing plan.
    ///
    /// Note that a key created by `butler login` is wharf-scoped: butlerd rejects it with
    /// "api key does not permit `profile:me`". A full-scope key from
    /// itch.io/user/settings/api-keys is required for delta updates.
    /// </summary>
    private static string? ReadItchApiKey()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ITCH_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        try
        {
            string credsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "itch", "butler_creds");

            if (File.Exists(credsPath))
            {
                string key = File.ReadAllText(credsPath).Trim();
                if (!string.IsNullOrWhiteSpace(key)) return key;
            }
        }
        catch
        {
            // No key available; the wharf source falls back to a full download.
        }

        return null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLogFatal(e.Exception, "Dispatcher");

        MessageBox.Show(
            $"{e.Exception.Message}\n\nThe launcher will keep running. Details in {LogPath}",
            "Birdbox Launcher — unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }

    private static void TryLogFatal(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            ServiceProvider?.GetService<ILogger<App>>()?.LogError(ex, "Unhandled ({Source}).", source);
        }
        catch
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} [FATAL] {source}: {ex}\n"); }
            catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Bounded wait: content sources shut down butlerd, and a hung daemon must not stop the
        // launcher from closing.
        if (ServiceProvider is ServiceProvider provider)
        {
            try { provider.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
            catch { /* shutting down anyway */ }
        }

        base.OnExit(e);
    }
}
