using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wafish.Storage;
using Wafish.Storage.Sqlite;

internal sealed class StorageReliabilityFixture : StorageConformanceChecks.IFixture
{
    public string Path { get; }
    public ServiceProvider Provider { get; }
    public IServiceProvider Services => Provider;
    private readonly bool _ownsPath;
    public StorageReliabilityFixture(string? path = null, string? password = null)
    {
        _ownsPath = path == null;
        Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wafish-storage-" + Guid.NewGuid().ToString("N") + ".db");
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWafishStorage();
        services.Configure<StorageOptions>(options => options.Sqlite.ConnectionString = new SqliteConnectionStringBuilder
        { DataSource = Path, Password = password ?? "", Pooling = false }.ToString());
        Provider = services.BuildServiceProvider();
    }
    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        if (_ownsPath) foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(Path + suffix);
    }
}
