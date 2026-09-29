using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Proxy;
using Harbora.Modules.Backup.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The panel under test must not reach for the machine's real directories.
///
/// <para>
/// Every option below ships with a default under <c>/var/lib/harbora</c> or <c>/etc/harbora</c>,
/// which is right for a server and wrong for a test run. Two failures made this test necessary, and
/// both were invisible until CI started loading again after seven weeks:
/// </para>
///
/// <list type="bullet">
/// <item><c>Backups:StagingDir</c> was never overridden. On Windows the path is drive-relative, so the
/// suite silently created <c>E:\var\lib\harbora\backups</c> on the developer's machine and filled it
/// with restored dumps. On a Linux runner it is a real system directory a non-root user cannot create,
/// so every restore and import test returned 500.</item>
/// <item>The work directory was overridden as <c>Harbora:WorkDir</c>, but
/// <see cref="HarboraRuntimeOptions"/> binds the <c>Runtime</c> section and nothing reads that key.
/// The override looked like protection and bound nothing.</item>
/// </list>
///
/// <para>
/// So this reads each value back out of the <b>bound</b> options rather than trusting the
/// configuration keys that were set — the second failure is exactly a key that was set and ignored.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class HarboraWebFactoryIsolationTests(HarboraHttpFixture fixture)
{
    private T Bound<T>() where T : class => fixture.Panel.Services.GetRequiredService<IOptions<T>>().Value;

    public static TheoryData<string> Paths() => new()
    {
        "Runtime:WorkDir",
        "Backups:StagingDir",
        "Traefik:DynamicConfigPath",
        "Backups:Kopia:ConfigDirectory",
        "Backups:Kopia:CacheDirectory",
        "Backups:Module:RestoreRoot",
        "Backups:Module:StagingDirectory"
    };

    private string Resolve(string key) => key switch
    {
        "Runtime:WorkDir" => Bound<HarboraRuntimeOptions>().WorkDir,
        "Backups:StagingDir" => Bound<BackupOptions>().StagingDir,
        "Traefik:DynamicConfigPath" => Bound<TraefikOptions>().DynamicConfigPath,
        "Backups:Kopia:ConfigDirectory" => Bound<KopiaOptions>().ConfigDirectory,
        "Backups:Kopia:CacheDirectory" => Bound<KopiaOptions>().CacheDirectory,
        "Backups:Module:RestoreRoot" => Bound<BackupModuleOptions>().RestoreRoot,
        "Backups:Module:StagingDirectory" => Bound<BackupModuleOptions>().StagingDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_system_path_the_panel_uses_is_inside_this_runs_temp_directory(string key)
    {
        var resolved = Path.GetFullPath(Resolve(key));
        var temp = Path.GetFullPath(Path.GetTempPath());

        resolved.Should().StartWith(temp,
            $"{key} resolved to {resolved}: a test run must never write to the machine's real " +
            "/var/lib or /etc (or, on Windows, to a drive-relative copy of them), and a value that is " +
            "still the shipped default means the override for it binds nothing");
    }
}
