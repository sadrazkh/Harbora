using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Who can see and use which template.
///
/// A template runs someone else's container image inside a tenant's private network, next to their
/// database. That makes appearing in the shared catalog a decision a person makes, not something
/// that happens because a form was saved — and it makes "who can see this" a rule worth writing down
/// once, with tests, rather than a Where clause copied into three screens.
/// </summary>
public static class TemplateCatalog
{
    /// <summary>
    /// Whether this workspace may deploy this template: the ones Harbora ships, the ones an admin
    /// approved, and its own — including its own while they are waiting for review or were sent
    /// back, since it wrote them and already trusts them.
    /// </summary>
    public static bool IsVisibleTo(AppTemplate template, Guid workspaceId) =>
        template.IsEnabled
        && (template.WorkspaceId is null
            || template.WorkspaceId == workspaceId
            || template.Status == TemplateStatus.Approved);

    /// <summary>
    /// Whether it is offered to everyone. Deliberately narrower than <see cref="IsVisibleTo"/>: a
    /// template is in the shared catalog only once someone approved it.
    /// </summary>
    public static bool IsInSharedCatalog(AppTemplate template) =>
        template.IsEnabled
        && (template.WorkspaceId is null || template.Status == TemplateStatus.Approved);

    /// <summary>
    /// Whether this workspace may change it. A shipped template is not editable by a tenant, and an
    /// approved one is not editable behind the approval — that would make review meaningless.
    /// </summary>
    public static bool CanEdit(AppTemplate template, Guid workspaceId) =>
        template.WorkspaceId == workspaceId && template.Status != TemplateStatus.Approved;

    /// <summary>Whether it can be sent for review — only from a state where review is the next step.</summary>
    public static bool CanSubmit(AppTemplate template, Guid workspaceId) =>
        template.WorkspaceId == workspaceId
        && template.Status is TemplateStatus.Private or TemplateStatus.Rejected;

    /// <summary>One word for the state, for the badge next to the name.</summary>
    public static string Describe(AppTemplate template) => template switch
    {
        { WorkspaceId: null } => "Built in",
        { Status: TemplateStatus.Approved } => "In the shared catalog",
        { Status: TemplateStatus.Submitted } => "Waiting for review",
        { Status: TemplateStatus.Rejected } => "Sent back",
        _ => "Yours only"
    };
}
