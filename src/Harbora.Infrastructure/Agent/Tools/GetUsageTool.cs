using System.Text.Json;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>get_usage</c> — the workspace's plan and how much of each limit it uses
/// (<see cref="IQuotaService.GetUsageAsync"/>, the same figures the usage page shows), and how many
/// assistant requests are left today. A limit of 0 means unlimited, as it does everywhere in quotas.
/// </summary>
public sealed class GetUsageTool(IQuotaService quotas, AgentUsageMeter meter) : AgentReadTool
{
    private const long Megabyte = 1024 * 1024;

    public override string Name => "get_usage";

    public override string Description =>
        "Shows the workspace's plan and its usage against each limit: apps, databases, memory, CPU, disk, projects, " +
        "environments, domains, volumes, cron jobs and concurrent deployments (a max of 0 means unlimited), plus how many " +
        "assistant requests are left today. Use it before proposing something that may exceed a limit.";

    public override string ParametersSchema =>
        """
        { "type": "object", "properties": {}, "additionalProperties": false }
        """;

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var u = await quotas.GetUsageAsync(context.WorkspaceId, ct);

        var payload = new
        {
            plan = u.PlanName,
            suspended = u.Suspended,
            apps = new { used = u.Apps, max = u.MaxApps },
            databases = new { used = u.Services, max = u.MaxServices },
            memoryMb = new { used = u.MemoryUsedBytes / Megabyte, max = u.MaxMemoryBytes / Megabyte },
            cpuCores = new { used = u.CpuUsed, max = u.MaxCpuCores },
            diskMb = new { used = u.DiskUsedBytes / Megabyte, max = u.MaxDiskBytes / Megabyte, unmeasured = u.DiskUnmeasured },
            projects = new { used = u.Projects, max = u.MaxProjects },
            environments = new { used = u.Environments, max = u.MaxEnvironments },
            domains = new { used = u.Domains, max = u.MaxDomains },
            volumes = new { used = u.Volumes, max = u.MaxVolumes },
            cronJobs = new { used = u.CronJobs, max = u.MaxCronJobs },
            concurrentDeployments = new { used = u.ActiveDeployments, max = u.MaxConcurrentDeployments },
            assistantRequestsLeftToday = await meter.RemainingAsync(context.WorkspaceId, ct)
        };

        return Found(payload, "Read usage and limits", "مصرف و سقف‌ها بررسی شد");
    }
}
