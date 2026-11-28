using System.Text.Json.Serialization;

namespace Wafish.Storage;

/// <summary>Optional persistence for private operator preferences. Owners are opaque, authenticated identity hashes.</summary>
public interface IOperatorViewRepository
{
    /// <summary>Returns the owner's views for one workspace, ordered by name and identifier. Observes caller cancellation.</summary>
    Task<IReadOnlyList<OperatorView>> ListAsync(string owner, string workspace, CancellationToken cancellationToken = default);
    /// <summary>Atomically saves a validated view at the expected revision. Zero creates; positive revisions update.</summary>
    Task<OperatorViewWriteResult> SaveAsync(string owner, OperatorView view, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>Deletes only an owned view at the expected revision. Missing views return NotFound.</summary>
    Task<OperatorViewWriteResult> DeleteAsync(string owner, string workspace, Guid id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>Explicit preferences only. Contains no credentials, row data, request headers, or session tokens.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorViewSettings(string Search, string Filter, string Route, int? MinimumStatus, bool Historical, string[] Columns);
/// <summary>A named private view. Revision zero is reserved for a new view.</summary>
public sealed record OperatorView(Guid Id, string Workspace, string Name, OperatorViewSettings Settings, bool IsDefault, long Revision);
public enum OperatorViewWriteOutcome { Saved, Deleted, Conflict, NameConflict, NotFound, Capacity }
public sealed record OperatorViewWriteResult(OperatorViewWriteOutcome Outcome, OperatorView? View = null);

/// <summary>Shared bounds for optional providers and administration clients.</summary>
public static class OperatorViewRules
{
    public const int MaximumPerOwner = 50;
    public const int MaximumTotal = 4096;
    public static bool IsOwner(string? owner) => owner is { Length: 64 } && owner.All(char.IsAsciiHexDigit);
    public static bool IsWorkspace(string? value) => value is { Length: >= 1 and <= 64 } && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    public static bool IsValid(OperatorView? view) => view is { Id: var id, Name: { Length: >= 1 and <= 64 } name, Revision: >= 0, Settings: { } settings } &&
        id != Guid.Empty && IsWorkspace(view.Workspace) && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl) &&
        settings.Search is { Length: <= 256 } && !settings.Search.Any(char.IsControl) && settings.Filter is { Length: <= 256 } && settings.Route is { Length: <= 128 } &&
        !settings.Filter.Any(char.IsControl) && !settings.Route.Any(char.IsControl) &&
        settings.MinimumStatus is null or >= 100 and <= 599 && settings.Columns is { Length: >= 1 and <= 32 } columns &&
        columns.All(column => column is { Length: >= 1 and <= 64 } && column.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) &&
        columns.Distinct(StringComparer.Ordinal).Count() == columns.Length;
}
