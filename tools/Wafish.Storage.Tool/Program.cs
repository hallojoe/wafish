using System.Text.Json;
using Wafish.Storage.Sqlite;

if (args is not ["inspect" or "verify", _])
{
    Console.Error.WriteLine("Usage: Wafish.Storage.Tool <inspect|verify> <standalone-backup-path>");
    Console.Error.WriteLine("Set WAFISH_BACKUP_PASSWORD for encrypted backups. Do not pass keys as arguments.");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var password = Environment.GetEnvironmentVariable("WAFISH_BACKUP_PASSWORD");
    var report = args[0] == "inspect"
        ? await SqliteBackupInspector.InspectAsync(args[1], password, cancellation.Token)
        : await SqliteBackupInspector.VerifyRestoreAsync(args[1], password, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return report.Valid ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Inspection cancelled."); return 130; }
