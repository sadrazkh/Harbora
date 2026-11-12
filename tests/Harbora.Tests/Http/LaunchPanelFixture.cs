using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Launch;
using Harbora.Shared.Inspection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One booted panel for the launch pages: the real pipeline, the real views, and a scripted
/// <see cref="ILaunchService"/>. Its own host rather than the shared one, because the shared host has
/// no seam for replacing a service; built through <c>WithWebHostBuilder</c> on a
/// <see cref="HarboraWebFactory"/>, so every substitution that factory documents still applies.
/// </summary>
public sealed class LaunchPanelFixture : IDisposable
{
    private readonly HarboraWebFactory _root = new();
    private int _nextAddress;

    public WebApplicationFactory<Program> Panel { get; }
    public FakeLaunchService Launches { get; } = new();

    /// <summary>
    /// The connected accounts' provider API. Only a workspace a test connects an account in ever asks it,
    /// so the shared workspace's pages are the paste field they always were.
    /// </summary>
    public Harbora.Tests.Fakes.FakeGitProviders GitProviders { get; } = new();
    public RecordingDeploymentEngine Deployments => _root.Deployments;

    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid OtherWorkspaceId { get; } = Guid.CreateVersion7();
    public Guid EnvironmentId { get; } = Guid.CreateVersion7();

    public string WorkDir => Panel.Services.GetRequiredService<IOptions<HarboraRuntimeOptions>>().Value.WorkDir;

    public LaunchPanelFixture()
    {
        Panel = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILaunchService>();
            services.AddScoped<ILaunchService>(provider =>
                new WorkspaceScopedLaunches(Launches, provider.GetRequiredService<ICurrentUser>()));
            services.RemoveAll<IGitProviderClient>();
            services.AddSingleton<IGitProviderClient>(GitProviders);
        }));

        Seed(db =>
        {
            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });
            db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = "apps.example.com" });

            foreach (var (id, slug) in new[] { (WorkspaceId, "launch-main"), (OtherWorkspaceId, "launch-other") })
                db.Workspaces.Add(new Workspace { Id = id, Name = slug, Slug = slug, IsDefault = id == WorkspaceId });

            var projectId = Guid.CreateVersion7();
            db.Projects.Add(new Harbora.Domain.Projects.Project { Id = projectId, WorkspaceId = WorkspaceId, Name = "Shop", Slug = "launch-shop" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = EnvironmentId, WorkspaceId = WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });
    }

    public void Seed(Action<HarboraDbContext> seed)
    {
        using var scope = Panel.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        seed(db);
        db.SaveChanges();
    }

    /// <summary>A brand-new workspace with nothing in it, for the first-run card and the empty lists.</summary>
    public Guid GivenEmptyWorkspace(string slug)
    {
        var id = Guid.CreateVersion7();
        Seed(db => db.Workspaces.Add(new Workspace { Id = id, Name = slug, Slug = slug }));
        return id;
    }

    public async Task<HttpClient> SignedInAs(Guid workspaceId, string email, SystemRole role = SystemRole.Owner)
    {
        using (var scope = Panel.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var user = new User
            {
                Email = email, DisplayName = email, PasswordHash = hasher.Hash(HarboraWebFactory.TestPassword),
                Role = role, IsActive = true, EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            Seed(db =>
            {
                db.Users.Add(user);
                db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    WorkspaceId = workspaceId, UserId = user.Id,
                    Role = role switch
                    {
                        SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
                        SystemRole.Viewer => WorkspaceRole.Viewer,
                        _ => WorkspaceRole.Member
                    }
                });
            });
        }

        var client = Panel.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true
        });
        client.DefaultRequestHeaders.Add(HarboraWebFactory.RemoteIpHeader, $"203.0.113.{Interlocked.Increment(ref _nextAddress)}");

        var token = await client.AntiforgeryTokenFrom("/account/login");
        var login = await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));
        login.StatusCode.Should().Be(HttpStatusCode.Found, "the sign-in should succeed");
        return client;
    }

    /// <summary>A user in <paramref name="workspaceId"/> and the plaintext of a CLI token for them, issued the way the panel issues one.</summary>
    public string GivenToken(Guid workspaceId, string email, SystemRole role = SystemRole.Owner)
    {
        using var scope = Panel.Services.CreateScope();
        var user = new User
        {
            Email = email, DisplayName = email, PasswordHash = "not-signing-in", Role = role, IsActive = true,
            EmailVerifiedAt = DateTimeOffset.UtcNow
        };
        var issued = scope.ServiceProvider.GetRequiredService<ITokenService>().Issue(user.Id, "test", TokenType.Cli, null);
        Seed(db =>
        {
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspaceId, UserId = user.Id,
                Role = role switch
                {
                    SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
                    SystemRole.Viewer => WorkspaceRole.Viewer,
                    _ => WorkspaceRole.Member
                }
            });
            db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "test", Prefix = issued.Prefix, TokenHash = issued.Hash, Type = TokenType.Cli });
        });
        return issued.PlaintextToken;
    }

    /// <summary>The CLI's shape of request: a bearer token and nothing else.</summary>
    public HttpClient BearerClient(string token)
    {
        var client = Panel.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = false
        });
        client.DefaultRequestHeaders.Add(HarboraWebFactory.RemoteIpHeader, $"203.0.113.{Interlocked.Increment(ref _nextAddress)}");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public void Dispose()
    {
        Panel.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Stands in for the real service's one workspace rule: <see cref="ILaunchService.GetAsync"/> reads
    /// only the caller's. Nothing else here filters, on purpose — so a route that forgot to ask first
    /// would reach another workspace's launch in these tests, which is what they are looking for.
    /// </summary>
    private sealed class WorkspaceScopedLaunches(FakeLaunchService fake, ICurrentUser currentUser) : ILaunchService
    {
        public Task<LaunchView?> GetAsync(Guid launchId, CancellationToken ct) =>
            Task.FromResult(fake.Get(launchId, currentUser.WorkspaceId));

        public Task<ServiceOutcome<Guid>> StartAsync(LaunchStartRequest request, CancellationToken ct) => fake.StartAsync(request);
        public Task InspectAsync(Guid launchId, CancellationToken ct) => Task.CompletedTask;
        public Task<LaunchProposal?> ProposeAsync(Guid launchId, CancellationToken ct) => Task.FromResult(fake.Propose(launchId));
        public Task<ServiceOutcome<LaunchProposal>> ChooseRootAsync(Guid launchId, string candidate, CancellationToken ct) => Task.FromResult(fake.ChooseRoot(launchId, candidate));
        public Task<LaunchQuote> QuoteAsync(Guid launchId, LaunchChoices choices, CancellationToken ct) => Task.FromResult(fake.Quote(launchId, choices));
        public Task<IReadOnlyList<ServiceRefusal>> ValidateConfirmAsync(Guid launchId, LaunchChoices choices, CancellationToken ct) => Task.FromResult(fake.Validate(launchId, choices));
        public Task<ServiceOutcome<LaunchView>> ConfirmAsync(Guid launchId, LaunchChoices choices, string? clientIp, CancellationToken ct) => Task.FromResult(fake.Confirm(launchId, choices));
        public Task<ServiceOutcome<LaunchView>> RetryAsync(Guid launchId, string? clientIp, CancellationToken ct) => Task.FromResult(fake.Retry(launchId));
        public Task<bool> AbandonAsync(Guid launchId, CancellationToken ct) => Task.FromResult(fake.Abandon(launchId));
        public Task<int> AdvanceAllAsync(CancellationToken ct) => Task.FromResult(0);
    }
}

/// <summary>Scripted launches, and a record of what the pages asked of them.</summary>
public sealed class FakeLaunchService
{
    private sealed class Entry
    {
        public required Guid WorkspaceId { get; init; }
        public required LaunchView View { get; set; }
        public LaunchProposal? Proposal { get; set; }
        public LaunchQuote? Quote { get; init; }

        /// <summary>What the next reads find, one each: the tick, scripted.</summary>
        public Queue<LaunchView> Next { get; } = new();

        /// <summary>What joins <see cref="Next"/> the moment it is confirmed.</summary>
        public List<LaunchView> AfterConfirm { get; } = [];
    }

    private readonly Dictionary<Guid, Entry> _launches = [];
    private readonly object _gate = new();

    public List<LaunchStartRequest> Started { get; } = [];
    public List<(Guid Id, string Candidate)> ChosenRoots { get; } = [];
    public List<(Guid Id, LaunchChoices Choices)> Quoted { get; } = [];
    public List<(Guid Id, LaunchChoices Choices)> Validated { get; } = [];
    public List<(Guid Id, LaunchChoices Choices)> Confirmed { get; } = [];
    public List<Guid> Retried { get; } = [];
    public List<Guid> Abandoned { get; } = [];

    /// <summary>How many times a launch has been looked at, by anything.</summary>
    public int Reads { get; private set; }

    /// <summary>What <c>StartAsync</c> refuses with next, or null to succeed.</summary>
    public ServiceRefusal? StartRefusal { get; set; }

    /// <summary>Makes <c>StartAsync</c> blow up, the way a database that went away would.</summary>
    public bool StartThrows { get; set; }

    public Guid NextStartId { get; set; } = Guid.CreateVersion7();

    public IReadOnlyList<ServiceRefusal> ValidateRefusals { get; set; } = [];
    public IReadOnlyList<ServiceRefusal> ConfirmRefusals { get; set; } = [];
    public LaunchStatus? StatusAfterRefusedConfirm { get; set; }
    public IReadOnlyList<ServiceRefusal> RetryRefusals { get; set; } = [];
    public bool AbandonAllowed { get; set; } = true;

    public void Reset()
    {
        lock (_gate)
        {
            _launches.Clear();
            Started.Clear(); ChosenRoots.Clear(); Quoted.Clear(); Validated.Clear(); Confirmed.Clear(); Retried.Clear(); Abandoned.Clear();
            Reads = 0;
            StartRefusal = null; StartThrows = false; NextStartId = Guid.CreateVersion7();
            ValidateRefusals = []; ConfirmRefusals = []; RetryRefusals = []; AbandonAllowed = true;
            StatusAfterRefusedConfirm = null;
        }
    }

    public void Add(Guid workspaceId, LaunchView view, LaunchProposal? proposal = null, LaunchQuote? quote = null)
    {
        lock (_gate) _launches[view.Id] = new Entry { WorkspaceId = workspaceId, View = view, Proposal = proposal, Quote = quote };
    }

    /// <summary>Scripts what this launch turns into, one state per read.</summary>
    public void Then(Guid id, params LaunchView[] views)
    {
        lock (_gate) foreach (var view in views) _launches[id].Next.Enqueue(view);
    }

    /// <summary>Scripts what this launch turns into once it has been confirmed, one state per read.</summary>
    public void ThenAfterConfirm(Guid id, params LaunchView[] views)
    {
        lock (_gate) _launches[id].AfterConfirm.AddRange(views);
    }

    public LaunchView? Get(Guid id, Guid? workspaceId)
    {
        lock (_gate)
        {
            Reads++;
            if (!_launches.TryGetValue(id, out var entry) || entry.WorkspaceId != workspaceId) return null;
            if (entry.Next.TryDequeue(out var next)) entry.View = next;
            return entry.View;
        }
    }

    public Task<ServiceOutcome<Guid>> StartAsync(LaunchStartRequest request)
    {
        lock (_gate)
        {
            Started.Add(request);
            if (StartThrows) throw new InvalidOperationException("the database went away");
            return Task.FromResult(StartRefusal is { } refusal
                ? ServiceOutcome<Guid>.Refused(refusal)
                : ServiceOutcome<Guid>.Success(NextStartId));
        }
    }

    public LaunchProposal? Propose(Guid id)
    {
        lock (_gate) return _launches.TryGetValue(id, out var entry) && entry.View.Status == LaunchStatus.AwaitingConfirmation ? entry.Proposal : null;
    }

    public ServiceOutcome<LaunchProposal> ChooseRoot(Guid id, string candidate)
    {
        lock (_gate)
        {
            ChosenRoots.Add((id, candidate));
            var entry = _launches[id];
            var proposal = entry.Proposal!;
            if (!proposal.Inspection.RootCandidates.Contains(candidate))
                return ServiceOutcome<LaunchProposal>.Refused(
                    new ServiceRefusal("That folder isn't in this repository.", "این پوشه در این مخزن نیست."));

            entry.Proposal = proposal with { Inspection = proposal.Inspection with { RootDirectory = candidate } };
            return ServiceOutcome<LaunchProposal>.Success(entry.Proposal);
        }
    }

    public LaunchQuote Quote(Guid id, LaunchChoices choices)
    {
        lock (_gate)
        {
            Quoted.Add((id, choices));
            return _launches[id].Quote ?? new LaunchQuote([], 0, null, "USD", Exempt: true, Refusal: null);
        }
    }

    public IReadOnlyList<ServiceRefusal> Validate(Guid id, LaunchChoices choices)
    {
        lock (_gate)
        {
            Validated.Add((id, choices));
            return ValidateRefusals;
        }
    }

    public ServiceOutcome<LaunchView> Confirm(Guid id, LaunchChoices choices)
    {
        lock (_gate)
        {
            Confirmed.Add((id, choices));
            if (ConfirmRefusals.Count > 0)
            {
                if (StatusAfterRefusedConfirm is { } status) _launches[id].View = _launches[id].View with { Status = status };
                return ServiceOutcome<LaunchView>.Refused(ConfirmRefusals);
            }
            _launches[id].View = _launches[id].View with { Status = LaunchStatus.Creating };
            foreach (var view in _launches[id].AfterConfirm) _launches[id].Next.Enqueue(view);
            return ServiceOutcome<LaunchView>.Success(_launches[id].View);
        }
    }

    public ServiceOutcome<LaunchView> Retry(Guid id)
    {
        lock (_gate)
        {
            Retried.Add(id);
            return RetryRefusals.Count > 0
                ? ServiceOutcome<LaunchView>.Refused(RetryRefusals)
                : ServiceOutcome<LaunchView>.Success(_launches[id].View);
        }
    }

    public bool Abandon(Guid id)
    {
        lock (_gate)
        {
            Abandoned.Add(id);
            return AbandonAllowed;
        }
    }
}
