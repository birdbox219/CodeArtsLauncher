using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;

namespace Launcher.Core.Services;

public class LocalJsonConfigService : IConfigService
{
    private readonly string _configFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LocalJsonConfigService() : this(null) { }

    public LocalJsonConfigService(string? customConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(customConfigPath))
        {
            _configFilePath = customConfigPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "MyGameLauncher");
            _configFilePath = Path.Combine(folder, "config.json");
        }
    }

    public async Task<LauncherConfig> LoadConfigAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                using var stream = File.OpenRead(_configFilePath);
                var config = await JsonSerializer.DeserializeAsync<LauncherConfig>(stream, JsonOptions, ct);
                if (config != null)
                {
                    EnsureDefaults(config);
                    return config;
                }
            }
        }
        catch
        {
            // Fallback to defaults on error
        }

        var defaultConfig = new LauncherConfig();
        EnsureDefaults(defaultConfig);
        return defaultConfig;
    }

    public async Task SaveConfigAsync(LauncherConfig config, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(_configFilePath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions, ct);
    }

    private static void EnsureDefaults(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseInstallDirectory))
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            config.BaseInstallDirectory = Path.Combine(localApp, "MyGameLauncher", "Games");
        }

        // A config written by an older build has no profile name (it stored the Windows user name
        // under a different key), which would otherwise leave the library permanently empty.
        if (string.IsNullOrWhiteSpace(config.ItchProfileUsername))
        {
            config.ItchProfileUsername = new LauncherConfig().ItchProfileUsername;
        }

        if (string.IsNullOrWhiteSpace(config.DefaultChannel))
        {
            config.DefaultChannel = "windows";
        }
    }
}
