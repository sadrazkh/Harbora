using System.Text;
using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IContainerConfigFileWriter"/> — C2 (2026-08-22 config-delivery plan).
/// Keyed by path only, not by container id: every pipeline test that uses this exercises one app's
/// containers at a time, so there is no ambiguity a per-container keying would resolve that this one
/// does not already handle, and keeping it simple makes seeding a test's starting file trivial.
/// </summary>
public sealed class FakeContainerConfigFileWriter : IContainerConfigFileWriter
{
    private readonly Dictionary<string, byte[]> _filesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _directoriesByPath = new(StringComparer.Ordinal);

    /// <summary>Every write, in order — what the pipeline actually applied, and to which container.</summary>
    public List<(string ContainerNameOrId, string Path, string Content)> Writes { get; } = [];

    public FakeContainerConfigFileWriter SeedFile(string absolutePath, string content)
    {
        _filesByPath[absolutePath] = Encoding.UTF8.GetBytes(content);
        return this;
    }

    public FakeContainerConfigFileWriter SeedDirectory(string absolutePath, params string[] entries)
    {
        _directoriesByPath[absolutePath] = entries;
        return this;
    }

    public string? CurrentFileContent(string absolutePath) =>
        _filesByPath.TryGetValue(absolutePath, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public Task<byte[]?> ReadFileAsync(string containerNameOrId, string absolutePath, CancellationToken ct) =>
        Task.FromResult(_filesByPath.TryGetValue(absolutePath, out var bytes) ? bytes : null);

    public Task WriteFileAsync(string containerNameOrId, string absolutePath, byte[] content, CancellationToken ct)
    {
        _filesByPath[absolutePath] = content;
        Writes.Add((containerNameOrId, absolutePath, Encoding.UTF8.GetString(content)));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> ListDirectoryAsync(string containerNameOrId, string absoluteDirectoryPath, CancellationToken ct) =>
        Task.FromResult(_directoriesByPath.TryGetValue(absoluteDirectoryPath, out var entries) ? entries : null);
}
