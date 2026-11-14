using System.Text.Json;

namespace Wafish.Dashboard.Infrastructure.Auth;

/// <summary>Atomic, private persistence for the dashboard's single-account TOTP state.</summary>
public static class TwoFactorStateStore
{
    private static readonly object Sync = new();
    public sealed record State([property: System.Text.Json.Serialization.JsonRequired] bool Enabled, [property: System.Text.Json.Serialization.JsonRequired] string? Secret);

    public static State Read(string path, State fallback)
    {
        lock (Sync)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return fallback; }
            catch (DirectoryNotFoundException) { return fallback; }
            var state = JsonSerializer.Deserialize<State>(json) ?? throw new InvalidDataException("Invalid two-factor state.");
            if (state.Enabled && string.IsNullOrWhiteSpace(state.Secret))
            {
                throw new InvalidDataException("Missing two-factor secret.");
            }

            return state;
        }
    }

    public static void Write(string path, State state)
    {
        lock (Sync)
        {
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".twofactor-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(temporary, options))
                {
                    JsonSerializer.Serialize(stream, state);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
