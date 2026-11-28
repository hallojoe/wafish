using Wafish.Storage;

namespace Wafish.Dashboard.Modules.Dashboard.Services;

/// <summary>Optional private preferences. These writes do not publish gateway configuration.</summary>
public interface IOperatorViewService
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<OperatorView>> ListAsync(string owner, string workspace, CancellationToken cancellationToken);
    Task<OperatorViewWriteResult> SaveAsync(string owner, OperatorView view, long expectedRevision, CancellationToken cancellationToken);
    Task<OperatorViewWriteResult> DeleteAsync(string owner, string workspace, Guid id, long expectedRevision, CancellationToken cancellationToken);
}

internal sealed class OperatorViewService(IEnumerable<IOperatorViewRepository> repositories) : IOperatorViewService
{
    private readonly IOperatorViewRepository? _repository = repositories.LastOrDefault();
    public bool IsAvailable => _repository is not null;
    private IOperatorViewRepository Repository => _repository ?? throw new InvalidOperationException("Operator view persistence is unavailable.");
    public Task<IReadOnlyList<OperatorView>> ListAsync(string owner, string workspace, CancellationToken cancellationToken) => Repository.ListAsync(owner, workspace, cancellationToken);
    public Task<OperatorViewWriteResult> SaveAsync(string owner, OperatorView view, long expectedRevision, CancellationToken cancellationToken)
    {
        if (!OperatorViewRules.IsValid(view) || expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("Invalid operator view.");
        return Repository.SaveAsync(owner, view with { Settings = view.Settings with { Columns = view.Settings.Columns.ToArray() } }, expectedRevision, cancellationToken);
    }
    public Task<OperatorViewWriteResult> DeleteAsync(string owner, string workspace, Guid id, long expectedRevision, CancellationToken cancellationToken) =>
        Repository.DeleteAsync(owner, workspace, id, expectedRevision, cancellationToken);
}
