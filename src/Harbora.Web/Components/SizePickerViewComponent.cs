using Harbora.Application.Abstractions;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Components;

/// <summary>
/// The shared size chooser, drawn wherever a form needs one.
///
/// <para>
/// A view component rather than a partial fed from a controller, and deliberately so. The chooser
/// needs the workspace's plan, every tier, every server, every per-server price and a live capacity
/// reading — five reads that have nothing to do with what the surrounding form is about. Threading
/// them through four controllers would have added a constructor dependency to each, and every test
/// that builds one of those controllers would have had to construct a chooser to ask an unrelated
/// question. This is the pattern <c>AccountBalanceViewComponent</c> already uses for the same reason.
/// </para>
/// </summary>
public sealed class SizePickerViewComponent(
    SizePickerService picker,
    ICurrentUser currentUser) : ViewComponent
{
    /// <param name="sizeField">The form field the chosen tier's key is posted as.</param>
    /// <param name="serverField">
    /// The field the chosen host is posted as, or null when this form does not let the host be
    /// chosen — a resize keeps the workload where it is.
    /// </param>
    /// <param name="pinnedServer">
    /// The only host to offer. Set on a resize, for the same reason <paramref name="serverField"/> is
    /// null there: moving a workload between hosts severs its private network and has a confirmation
    /// screen of its own.
    /// </param>
    /// <param name="allowNoLimit">
    /// Whether "no ceiling" is a choice. True on a resize, where it is the state a resource made
    /// before tiers existed is already in; false on creation, where it would hand out a workload the
    /// meter has no rate for.
    /// </param>
    public async Task<IViewComponentResult> InvokeAsync(
        string sizeField,
        string? serverField = null,
        string? selectedSize = null,
        Guid? selectedServer = null,
        Guid? pinnedServer = null,
        bool allowNoLimit = false)
    {
        var model = await picker.BuildAsync(
            currentUser.WorkspaceId ?? Guid.Empty,
            sizeField,
            serverField,
            selectedSize,
            selectedServer,
            allowNoLimit,
            pinnedServer,
            HttpContext.RequestAborted);

        return View(model);
    }
}
