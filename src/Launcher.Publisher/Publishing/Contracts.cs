namespace Launcher.Publisher.Publishing;

/// <summary>Body of <c>POST /api/inspect</c>.</summary>
public sealed record InspectBody(string? Folder);

/// <summary>
/// Body of <c>POST /api/push</c>. <paramref name="Game"/> accepts a bare slug, <c>owner/slug</c>,
/// or a full <c>owner/slug:channel</c>, so the field can be typed as well as picked.
/// </summary>
public sealed record PushBody(
    string? Game,
    string? Owner,
    string? Channel,
    string? Folder,
    string? Version,
    bool DryRun,
    bool OnlyIfChanged);

/// <summary>What the panel knows about itself. Never carries the API key, only whether one exists.</summary>
public sealed record PanelState(
    string Profile,
    string ButlerPath,
    bool HasButlerCredentials,
    string CredentialsHint,
    string DefaultChannel,
    string? RunningJobId);

/// <summary>A push as the browser sees it, including enough history to survive a page reload.</summary>
public sealed record JobSnapshot(
    string Id,
    string Owner,
    string Slug,
    string Channel,
    string? Version,
    bool DryRun,
    bool Completed,
    PushOutcome? Outcome,
    PushEvent? LastProgress,
    IReadOnlyList<PushEvent> Events,
    DateTimeOffset StartedUtc)
{
    public static JobSnapshot From(PushJob job) => new(
        job.Id,
        job.Request.Owner,
        job.Request.Slug,
        job.Request.Channel,
        job.Request.Version,
        job.Request.DryRun,
        job.Completed,
        job.Outcome,
        job.LastProgress,
        job.Events,
        job.StartedUtc);
}
