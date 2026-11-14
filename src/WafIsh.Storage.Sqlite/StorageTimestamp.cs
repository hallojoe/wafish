using System.Globalization;
namespace WafIsh.Storage.Sqlite;

internal static class StorageTimestamp
{
    // Legacy values without offsets are interpreted as UTC, never as the machine's local zone.
    public static string Format(DateTime value) => (value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture);
    public static string Normalize(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
        ? instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : value;
}
