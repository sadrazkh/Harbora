using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Turns a persisted <see cref="Job"/> back into the work it describes. This is the piece that
/// replaces the old delegate-in-a-channel: a delegate can't be written to a database, but a
/// (kind, target) pair can, and this maps it back to a call.
/// </summary>
public static class JobDispatcher
{
    public static Task ExecuteAsync(Job job, IServiceProvider scope, CancellationToken ct) => job.Kind switch
    {
        JobKind.Deployment =>
            scope.GetRequiredService<DeploymentPipeline>().ExecuteAsync(job.TargetId, ct),

        JobKind.Backup =>
            scope.GetRequiredService<BackupEngine>().RunAsync(job.TargetId, ct),

        JobKind.ServiceProvision =>
            scope.GetRequiredService<ManagedServiceEngine>().ProvisionAsync(job.TargetId, ct),

        JobKind.CronRun =>
            scope.GetRequiredService<CronJobRunner>().RunAsync(job.TargetId, ct),

        _ => throw new NotSupportedException($"No handler is registered for job kind {job.Kind}.")
    };
}
