using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Tests.Fakes;

/// <summary>
/// A provider API that answers what a test says it holds, and records who asked with which token.
/// What the real client sends over the wire is <c>GitProviderClientTests</c>' business; this is for the
/// services that call it.
/// </summary>
public sealed class FakeGitProviders : IGitProviderClient
{
    /// <summary>What each token's account can see.</summary>
    public Dictionary<string, List<RemoteRepository>> Repositories { get; } = new(StringComparer.Ordinal);

    /// <summary>Thrown by every list, while set.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Thrown by a list asked with this token only, for a host shared by tests that must not see each other's failures.</summary>
    public Dictionary<string, Exception> Failing { get; } = new(StringComparer.Ordinal);

    public List<(GitProviderType Type, string ApiBaseUrl, string Token)> Listed { get; } = [];

    public FakeGitProviders Holding(string token, params RemoteRepository[] repositories)
    {
        Repositories[token] = [.. repositories];
        return this;
    }

    public Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderType type, string apiBaseUrl, string token, CancellationToken ct)
    {
        lock (Listed) Listed.Add((type, apiBaseUrl, token));
        if (FailWith is { } failure) throw failure;
        if (Failing.TryGetValue(token, out var own)) throw own;
        return Task.FromResult<IReadOnlyList<RemoteRepository>>(Repositories.TryGetValue(token, out var held) ? held : []);
    }
}
