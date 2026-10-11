using System.Text.Encodings.Web;
using System.Text.Json;
using Harbora.Infrastructure.Agent;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// How a page tells the assistant pane what "this" means — so "restart this app" asked on an app's
/// page has a referent.
///
/// <para>
/// Explicit rather than inferred: a page declares its context in one line, and <c>_Layout</c> renders
/// it as the drawer's <c>data-assistant-context</c> attribute, which the pane sends with every message
/// as <see cref="AgentPageContext"/>. Guessing ids out of the URL in the browser would be a second
/// router that disagrees with the real one the first time a route changes. The path itself is not
/// declared here: the pane sends <c>location.pathname</c>, which is always right.
/// </para>
/// </summary>
public static class AssistantPageContext
{
    public const string ViewDataKey = "AssistantContext";

    public static void Declare(
        ViewDataDictionary viewData,
        Guid? appId = null,
        Guid? serviceId = null,
        Guid? projectId = null,
        Guid? environmentId = null) =>
        viewData[ViewDataKey] = new AgentPageContext(null, appId, serviceId, projectId, environmentId);

    /// <summary>The declared context as the camelCase JSON the pane reads, or null when the page declared none.</summary>
    public static string? Json(ViewDataDictionary viewData) =>
        viewData[ViewDataKey] is AgentPageContext context
            ? JsonSerializer.Serialize(context, JsonSerializerOptions.Web)
            : null;

    /// <summary>
    /// The whole <c>data-assistant-context="…"</c> attribute, or nothing at all when the page declared
    /// no context. Written out rather than left to Razor's conditional attributes because those do not
    /// apply to <c>data-*</c> names: <c>data-x="@null"</c> renders <c>data-x=""</c>, and a list page
    /// would then claim an empty context instead of none.
    /// </summary>
    public static IHtmlContent Attribute(ViewDataDictionary viewData) =>
        Json(viewData) is { } json
            ? new HtmlString($"data-assistant-context=\"{HtmlEncoder.Default.Encode(json)}\"")
            : HtmlString.Empty;
}
