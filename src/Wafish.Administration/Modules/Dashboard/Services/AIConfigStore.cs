using System.Text.Json;
using Wafish.Dashboard.Infrastructure;
using Wafish.Storage;
using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Modules.Dashboard.Services;

/// <summary>
/// File-based AI config store. Loads from ai-config.json, falls back to bound IOptions.
/// Updates are persisted to the file and reflected in-memory immediately.
/// </summary>
public class AIConfigStore
{
    private static readonly JsonSerializerOptions FileJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IOptions<AIOptions> _boundOptions;
    private readonly string _configPath;
    private readonly object _lock = new();
    private AIOptions? _current;

    public AIConfigStore(IOptions<AIOptions> boundOptions, IOptions<StorageOptions> storageOptions)
    {
        _boundOptions = boundOptions;
        _configPath = ResolveConfigPath(storageOptions.Value);
        _current = LoadFromFile();
    }

    public AIOptions Current
    {
        get
        {
            lock (_lock)
            {
                return JsonSerializer.Deserialize<AIOptions>(JsonSerializer.Serialize(_current ?? _boundOptions.Value, FileJsonOpts), FileJsonOpts)!;
            }
        }
    }

    /// <summary>Path of the ai-config.json file this store reads/writes.</summary>
    public string ConfigPath => _configPath;

    public bool IsConfigured => Current.IsConfigured;

    public void Save(AIOptions options)
    {
        lock (_lock)
        {
            if (options.McpServer.Enabled && options.McpServer.Port is < 1 or > 65535)
            {
                throw new ArgumentException("MCP port must be between 1 and 65535.");
            }

            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(options, FileJsonOpts);
            var temporary = _configPath + "." + Guid.NewGuid().ToString("N");
            try
            {
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(temporary, fileOptions))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _configPath, overwrite: true);
                _current = JsonSerializer.Deserialize<AIOptions>(json, FileJsonOpts)!;
            }
            finally { if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private AIOptions? LoadFromFile()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return null;
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AIOptions>(json, FileJsonOpts);
        }
        catch { return null; }
    }

    private static string ResolveConfigPath(StorageOptions storage)
    {
        // Place ai-config.json next to the SQLite database
        var connStr = storage.Sqlite.ConnectionString;
        var dbFileName = "gateway-store.db";
        if (!string.IsNullOrEmpty(connStr))
        {
            var idx = connStr.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + "Data Source=".Length;
                var end = connStr.IndexOf(';', start);
                if (end < 0)
                {
                    end = connStr.Length;
                }

                dbFileName = connStr[start..end].Trim();
                if (!Path.IsPathRooted(dbFileName))
                {
                    dbFileName = Path.GetFullPath(dbFileName);
                }
            }
        }
        var dir = Path.GetDirectoryName(dbFileName);
        if (string.IsNullOrEmpty(dir))
        {
            dir = AppContext.BaseDirectory;
        }

        return Path.Combine(dir, "ai-config.json");
    }
}
