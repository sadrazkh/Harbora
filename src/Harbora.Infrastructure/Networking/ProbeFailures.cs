using System.Net.Sockets;
using System.Security.Authentication;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Decides whether a failed probe says something about the user's domain or about our own code.
///
/// This distinction is the difference between a useful panel and a confidently wrong one. A probe
/// that catches everything and reports "nothing answered on HTTPS" describes a broken domain, so a
/// bug in the probe itself reads as a bug in the user's DNS — which is exactly what happened: a
/// misconfigured SslStream threw InvalidOperationException on every single check, and the panel
/// calmly told the user to open port 443.
/// </summary>
public static class ProbeFailures
{
    /// <summary>
    /// True when the exception means the far end didn't answer — a fact about the domain. Everything
    /// else is ours to fix and must be surfaced, not translated into a verdict.
    /// </summary>
    public static bool IsConnectionFailure(Exception ex) => ex switch
    {
        SocketException => true,               // refused, unreachable, reset
        AuthenticationException => true,       // the handshake itself was rejected
        IOException => true,                   // the connection died mid-handshake
        OperationCanceledException => true,    // our own timeout: nothing answered in time
        _ => false
    };
}
