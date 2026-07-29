namespace Harbora.Web.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>HTTP status being reported (404, 403, 500 …).</summary>
    public int StatusCode { get; set; } = 500;

    /// <summary>The path the user actually asked for, when the framework can tell us.</summary>
    public string? OriginalPath { get; set; }

    /// <summary>Short headline, already localised by the view.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One sentence explaining what happened and what to do next.</summary>
    public string Detail { get; set; } = string.Empty;
}
