using WafIsh.Models;
namespace WafIsh.Services;

internal sealed class DeferredConfigChangeAuditLog(IConfigChangeAuditLog inner) : IConfigChangeAuditLog
{
    private readonly AsyncLocal<List<Action>?> _pending = new();
    public void Begin() => _pending.Value = [];
    public void Discard() => _pending.Value = null;
    public void Commit()
    {
        var pending = _pending.Value;
        Discard();
        if (pending != null) foreach (var record in pending) record();
    }
    public void RecordSuccess(string action, string target, string? operatorName = null, string? clientIp = null,
        object? before = null, object? after = null)
    {
        void Record() => inner.RecordSuccess(action, target, operatorName, clientIp, before, after);
        if (_pending.Value is { } pending) pending.Add(Record); else Record();
    }
    public event Action<string, string, string?, object?>? OnConfigChanged
    { add => inner.OnConfigChanged += value; remove => inner.OnConfigChanged -= value; }
    public void Record(ConfigChangeAudit entry) => inner.Record(entry);
    public void RecordFailure(string action, string target, string errorMessage, string? operatorName = null)
        => inner.RecordFailure(action, target, errorMessage, operatorName);
    public IReadOnlyList<ConfigChangeAudit> GetRecent(int count = 50) => inner.GetRecent(count);
    public (IReadOnlyList<ConfigChangeAudit> Entries, int Total) GetPage(int page = 1, int pageSize = 50, string? action = null)
        => inner.GetPage(page, pageSize, action);
    public int Count => inner.Count;
    public long EvictedCount => inner.EvictedCount;
    public bool TryDequeuePendingNotification(out PendingNotification notification) => inner.TryDequeuePendingNotification(out notification);
    public void InvokeOnConfigChanged(string eventType, string target, string? @operator, object? details)
        => inner.InvokeOnConfigChanged(eventType, target, @operator, details);
}
