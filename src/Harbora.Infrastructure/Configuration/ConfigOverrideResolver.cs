using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// Resolves and applies <see cref="ConfigOverrideRule"/>s — see <see cref="IConfigOverrideResolver"/>
/// for the contract. This is the single place a rule's four possible failure causes (file not found,
/// unparseable, key path absent, service reference gone) get turned into an actionable diagnostic,
/// used identically by <c>DeploymentPipeline</c> and by the panel's pre-deploy validation, so the two
/// can never disagree about why a rule would fail.
/// </summary>
public sealed class ConfigOverrideResolver(
    ConfigFileEditorFactory editors,
    IContainerConfigFileWriter files,
    ISecretProtector protector,
    IAttachedServiceConnectionStringResolver serviceResolver,
    ILogger<ConfigOverrideResolver> logger) : IConfigOverrideResolver
{
    public async Task ApplyAllAsync(App app, string containerNameOrId, CancellationToken ct)
    {
        var failures = new List<ConfigOverrideFailure>();
        var newContentByFile = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in app.ConfigOverrideRules.GroupBy(r => r.FilePath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var filePath = group.Key;
            var current = await ReadTextAsync(containerNameOrId, filePath, failures, group, ct);
            if (current is null) continue; // ReadTextAsync already recorded a failure for every rule in the group.

            var content = current;
            foreach (var rule in group.OrderBy(r => r.Order))
            {
                var outcome = await ResolveAndApplyAsync(app, rule, content, ct);
                if (outcome.Failure is not null) { failures.Add(outcome.Failure); continue; }
                content = outcome.NewContent!;
            }

            newContentByFile[filePath] = content;
        }

        if (failures.Count > 0) throw new ConfigOverrideException(failures);

        foreach (var (filePath, content) in newContentByFile)
        {
            await files.WriteFileAsync(containerNameOrId, filePath, Encoding.UTF8.GetBytes(content), ct);
            logger.LogInformation("Applied config override(s) to {Path} in {Container}.", filePath, containerNameOrId);
        }
    }

    public async Task<ConfigOverridePreview> PreviewAsync(App app, ConfigOverrideRule rule, string containerNameOrId, CancellationToken ct)
    {
        var failures = new List<ConfigOverrideFailure>();
        var content = await ReadTextAsync(containerNameOrId, rule.FilePath, failures, [rule], ct);
        if (content is null) return new ConfigOverridePreview(false, null, null, false, failures[0]);

        var format = ConfigFileFormatDetector.Resolve(rule.FilePath, rule.FormatOverride);
        if (format is null)
        {
            var failure = UndetectableFormatFailure(rule);
            return new ConfigOverridePreview(false, null, null, false, failure);
        }

        var editor = editors.For(format.Value);
        var inspection = editor.Inspect(content, rule.KeyPath);
        if (!inspection.Parsed)
        {
            var failure = new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                ConfigOverrideFailureReason.ParseError, $"could not be parsed as {format}: {inspection.ParseError}.");
            return new ConfigOverridePreview(false, null, null, false, failure);
        }

        if (!inspection.KeyFound)
        {
            var failure = new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                ConfigOverrideFailureReason.KeyPathNotFound, KeyPathNotFoundDetail(inspection.KeyPaths));
            return new ConfigOverridePreview(false, inspection.CurrentValue, null, false, failure);
        }

        var resolvedValue = await ResolveValueAsync(app, rule, ct);
        if (resolvedValue.Failure is not null)
            return new ConfigOverridePreview(false, inspection.CurrentValue, null, false, resolvedValue.Failure);

        var isSecret = rule.ValueKind is ConfigOverrideValueKind.Secret or ConfigOverrideValueKind.AttachedServiceConnectionString;
        return new ConfigOverridePreview(true, inspection.CurrentValue, isSecret ? null : resolvedValue.Value, isSecret, null);
    }

    private async Task<(string? NewContent, ConfigOverrideFailure? Failure)> ResolveAndApplyAsync(
        App app, ConfigOverrideRule rule, string content, CancellationToken ct)
    {
        var format = ConfigFileFormatDetector.Resolve(rule.FilePath, rule.FormatOverride);
        if (format is null) return (null, UndetectableFormatFailure(rule));

        var resolved = await ResolveValueAsync(app, rule, ct);
        if (resolved.Failure is not null) return (null, resolved.Failure);

        var editor = editors.For(format.Value);
        var outcome = editor.Apply(content, rule.KeyPath, resolved.Value!);

        if (outcome.ParseError is not null)
            return (null, new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                ConfigOverrideFailureReason.ParseError, $"could not be parsed as {format}: {outcome.ParseError}."));

        if (!outcome.Ok)
            return (null, new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                ConfigOverrideFailureReason.KeyPathNotFound, KeyPathNotFoundDetail(outcome.KeyPaths)));

        return (outcome.NewContent, null);
    }

    private async Task<(string? Value, ConfigOverrideFailure? Failure)> ResolveValueAsync(
        App app, ConfigOverrideRule rule, CancellationToken ct)
    {
        switch (rule.ValueKind)
        {
            case ConfigOverrideValueKind.Literal:
                return (rule.LiteralValue ?? string.Empty, null);

            case ConfigOverrideValueKind.Secret:
                return (SafeUnprotect(rule.EncryptedSecretValue), null);

            case ConfigOverrideValueKind.AttachedServiceConnectionString:
            {
                if (string.IsNullOrWhiteSpace(rule.AttachedServiceAlias))
                    return (null, new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                        ConfigOverrideFailureReason.ServiceReferenceUnavailable,
                        "this rule points at an attached service, but no alias is set."));

                var result = await serviceResolver.ResolveAsync(app.Id, rule.AttachedServiceAlias, ct);
                if (!result.Found)
                    return (null, new ConfigOverrideFailure(rule.Id, rule.FilePath, rule.KeyPath,
                        ConfigOverrideFailureReason.ServiceReferenceUnavailable,
                        result.FailureReason ?? "the referenced service is no longer attached."));

                return (result.ConnectionString, null);
            }

            default:
                throw new NotSupportedException($"Unknown config override value kind {rule.ValueKind}.");
        }
    }

    /// <summary>Reads the file once for a whole group of rules that share a path, recording the same
    /// file-not-found/unparseable failure against every rule in the group when it applies — a
    /// missing file breaks every rule that targets it, not just the first one tried.</summary>
    private async Task<string?> ReadTextAsync(
        string containerNameOrId, string filePath, List<ConfigOverrideFailure> failures,
        IEnumerable<ConfigOverrideRule> rulesForThisFile, CancellationToken ct)
    {
        byte[]? bytes;
        try { bytes = await files.ReadFileAsync(containerNameOrId, filePath, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {Path} from {Container} to apply config overrides.", filePath, containerNameOrId);
            bytes = null;
        }

        if (bytes is not null) return Encoding.UTF8.GetString(bytes);

        var directory = Posix.DirectoryName(filePath);
        var listing = await TryListDirectoryAsync(containerNameOrId, directory, ct);
        var detail = listing is null
            ? $"the file does not exist, and '{directory}' is not a directory in this container either."
            : listing.Count == 0
                ? $"the file does not exist; '{directory}' exists but is empty."
                : $"the file does not exist. '{directory}' actually has: {string.Join(", ", listing)}.";

        foreach (var rule in rulesForThisFile)
            failures.Add(new ConfigOverrideFailure(rule.Id, filePath, rule.KeyPath, ConfigOverrideFailureReason.FileNotFound, detail));

        return null;
    }

    private async Task<IReadOnlyList<string>?> TryListDirectoryAsync(string containerNameOrId, string directory, CancellationToken ct)
    {
        try { return await files.ListDirectoryAsync(containerNameOrId, directory, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list {Directory} in {Container}.", directory, containerNameOrId);
            return null;
        }
    }

    private static ConfigOverrideFailure UndetectableFormatFailure(ConfigOverrideRule rule) => new(
        rule.Id, rule.FilePath, rule.KeyPath, ConfigOverrideFailureReason.ParseError,
        $"'{rule.FilePath}' has no extension Harbora recognises — set an explicit format on this rule.");

    private static string KeyPathNotFoundDetail(IReadOnlyList<string> keyPaths) => keyPaths.Count == 0
        ? "this key path was not found, and the file has no keys at all."
        : "this key path was not found. This file's real key paths: " +
          string.Join(", ", keyPaths.Take(20)) + (keyPaths.Count > 20 ? $", and {keyPaths.Count - 20} more." : ".");

    private string SafeUnprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        try { return protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A config override's secret value could not be decrypted.");
            return string.Empty;
        }
    }

    private static class Posix
    {
        public static string DirectoryName(string path)
        {
            var normalised = path.Replace('\\', '/');
            var lastSlash = normalised.LastIndexOf('/');
            return lastSlash <= 0 ? "/" : normalised[..lastSlash];
        }
    }
}
