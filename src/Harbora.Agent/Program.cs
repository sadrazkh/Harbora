using Docker.DotNet;
using Harbora.Agent;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Docker;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Sync writes are needed to stream ordered log lines straight to the response body.
builder.WebHost.ConfigureKestrel(o => o.AllowSynchronousIO = true);

// The agent only needs the container runtime — not the panel's DB/EF stack.
builder.Services.AddSingleton<IDockerClient>(_ =>
{
    var host = builder.Configuration["Docker:Host"]
               ?? Environment.GetEnvironmentVariable("DOCKER_HOST")
               ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock");
    return new DockerClientConfiguration(new Uri(host)).CreateClient();
});
builder.Services.AddScoped<DockerEngine>();
builder.Services.AddScoped<IDockerEngine>(sp => sp.GetRequiredService<DockerEngine>());

// IncludeFields so the ValueTuple mounts in DockerRunRequest/DockerOneOffRequest bind from JSON.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.IncludeFields = true);

var app = builder.Build();

var token = app.Configuration["Agent:Token"] ?? Environment.GetEnvironmentVariable("HARBORA_AGENT_TOKEN");

// Bearer-token gate for everything except the health probe.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/agent/health")) { await next(); return; }
    var header = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(token) || header != $"Bearer {token}")
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/agent/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/agent/host", (IDockerEngine e, CancellationToken ct) => e.GetHostInfoAsync(ct));

app.MapGet("/agent/containers", (string? label, IDockerEngine e, CancellationToken ct) =>
    e.ListContainersAsync(label, ct));

app.MapGet("/agent/containers/{id}/stats", async (string id, IDockerEngine e, CancellationToken ct) =>
    await e.GetStatsAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapPost("/agent/containers/run", async (DockerRunRequest req, IDockerEngine e, CancellationToken ct) =>
    Results.Ok(new { id = await e.RunContainerAsync(req, ct) }));

app.MapPost("/agent/containers/{id}/stop", (string id, IDockerEngine e, CancellationToken ct) => e.StopContainerAsync(id, ct));
app.MapPost("/agent/containers/{id}/remove", (string id, bool force, IDockerEngine e, CancellationToken ct) => e.RemoveContainerAsync(id, force, ct));
app.MapPost("/agent/containers/{id}/restart", (string id, IDockerEngine e, CancellationToken ct) => e.RestartContainerAsync(id, ct));

app.MapGet("/agent/containers/{id}/logs", async (string id, HttpContext ctx, IDockerEngine e, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/plain";
    await using var writer = new StreamWriter(ctx.Response.Body);
    await e.StreamLogsAsync(id, new WriterProgress(writer), ct);
});

app.MapPost("/agent/images/pull", async (ImageBody body, HttpContext ctx, IDockerEngine e, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/plain";
    await using var writer = new StreamWriter(ctx.Response.Body);
    await e.PullImageAsync(body.Image, new WriterProgress(writer), ct);
});

app.MapPost("/agent/build", async (string tag, string dockerfile, HttpContext ctx, DockerEngine e, CancellationToken ct) =>
{
    var buildArgs = ParseBuildArgs(ctx.Request.Headers["X-Build-Args"].ToString());
    ctx.Response.ContentType = "text/plain";
    await using var writer = new StreamWriter(ctx.Response.Body);
    await e.BuildImageFromTarAsync(ctx.Request.Body, dockerfile, tag, buildArgs, new WriterProgress(writer), ct);
});

app.MapPost("/agent/networks/ensure", (NameBody b, IDockerEngine e, CancellationToken ct) => e.EnsureNetworkAsync(b.Name, ct));
app.MapPost("/agent/volumes/ensure", (NameBody b, IDockerEngine e, CancellationToken ct) => e.EnsureVolumeAsync(b.Name, ct));
app.MapPost("/agent/volumes/remove", (NameBody b, IDockerEngine e, CancellationToken ct) => e.RemoveVolumeAsync(b.Name, ct));

app.MapPost("/agent/oneoff", async (DockerOneOffRequest req, IDockerEngine e, CancellationToken ct) =>
    Results.Ok(new { exitCode = await e.RunOneOffAsync(req, null, ct) }));

app.Run();

static IReadOnlyDictionary<string, string> ParseBuildArgs(string header) =>
    string.IsNullOrWhiteSpace(header)
        ? new Dictionary<string, string>()
        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(header) ?? new();

namespace Harbora.Agent
{
    /// <summary>Writes engine log lines straight to the HTTP response in order.</summary>
    public sealed class WriterProgress(TextWriter writer) : IProgress<string>
    {
        private readonly Lock _gate = new();
        public void Report(string value)
        {
            lock (_gate) { writer.WriteLine(value); writer.Flush(); }
        }
    }

    public sealed record ImageBody(string Image);
    public sealed record NameBody(string Name);
}
