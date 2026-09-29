using System.Collections.Concurrent;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Maintenance;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The unattended, once-a-day dangling-image sweep.
///
/// <para>
/// Three promises are pinned here, each of which was a way the feature could have quietly failed: it
/// prunes dangling images and NOTHING else (an unattended job with a path to the orphan or retention
/// removals is one wrong argument from deleting a rollback image at three in the morning); it logs one
/// line per run including a run that reclaimed nothing (a sweep that logs only when it did something is
/// indistinguishable from one that never ran); and nothing it does can stop the panel — an unhandled
/// exception in a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> stops the host by default.
/// </para>
///
/// <para>
/// The loop tests use a real timer with waits of tens of milliseconds through the sweeper's internal
/// constructor, and poll for the outcome rather than sleeping for a fixed time. What is not exercised:
/// a full day passing, and a real Docker daemon behind the prune.
/// </para>
/// </summary>
public sealed class DanglingImageSweeperTests : IDisposable
{
    private readonly string _dbName = "dangling-sweeper-" + Guid.NewGuid();
    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;
    private readonly RecordingLogger<DanglingImageSweeper> _log = new();

    private readonly Lazy<ServiceProvider> _provider;

    public DanglingImageSweeperTests()
    {
        _engines = new FakeServerEngineFactory(_panel);
        _provider = new Lazy<ServiceProvider>(BuildProvider);
    }

    private ServiceProvider Provider() => _provider.Value;

    private ServiceProvider BuildProvider() => new ServiceCollection()
        .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_dbName))
        .AddSingleton<IServerEngineFactory>(_engines)
        .AddSingleton<IOptions<HarboraRuntimeOptions>>(Options.Create(new HarboraRuntimeOptions()))
        .AddSingleton<ILogger<DiskCleanupService>>(NullLogger<DiskCleanupService>.Instance)
        .AddScoped<DiskCleanupService>()
        .BuildServiceProvider();

    private async Task<Server> AddServerAsync(string name, bool local = false)
    {
        await using var scope = Provider().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var server = new Server { Id = Guid.NewGuid(), Name = name, IsLocal = local };
        db.Servers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private DanglingImageSweeper Sweeper(
        IServiceScopeFactory? scopes = null,
        HarboraRuntimeOptions? options = null,
        TimeSpan? startupDelay = null,
        TimeSpan? interval = null)
    {
        scopes ??= Provider().GetRequiredService<IServiceScopeFactory>();
        return new DanglingImageSweeper(
            scopes, Options.Create(options ?? new HarboraRuntimeOptions()), _log,
            startupDelay ?? TimeSpan.Zero, interval);
    }

    public void Dispose()
    {
        if (_provider.IsValueCreated) _provider.Value.Dispose();
    }

    // ---- what a run does ----

    [Fact]
    public async Task A_run_calls_only_the_dangling_prune_and_nothing_else()
    {
        await AddServerAsync("panel", local: true);
        _panel.SeedImage("mcr.microsoft.com/dotnet/sdk:10.0", "harbora/gone:build-1");
        _panel.SeedDangling(count: 165, bytesEach: 1000);

        var result = await Sweeper().RunOnceAsync(default);

        _panel.Calls.Select(c => c.Operation).Should().Equal(
            new[] { nameof(IDockerEngine.PruneDanglingImagesAsync) },
            "not orphans, not retention, no image named for removal — dangling images only");
        _panel.StoredImageTags.Should().BeEquivalentTo("mcr.microsoft.com/dotnet/sdk:10.0", "harbora/gone:build-1");
        result!.Removed.Should().Be(165);
    }

    [Fact]
    public async Task A_run_that_reclaimed_nothing_still_logs_exactly_one_line_saying_so()
    {
        await AddServerAsync("panel", local: true);

        await Sweeper().RunOnceAsync(default);

        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().StartWith(DanglingImageSweeper.LogPrefix)
            .And.Contain("pruned 0 dangling image(s)")
            .And.Contain("panel: 0 image(s), 0 B",
                "a machine that was asked and had nothing is reported as a zero, by name");
    }

    [Fact]
    public async Task A_run_logs_one_line_naming_what_it_reclaimed_on_each_server()
    {
        await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);
        _panel.SeedDangling(3, 1L << 30);
        secondDocker.SeedDangling(1, 512L << 20);

        await Sweeper().RunOnceAsync(default);

        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Message.Should().Contain("pruned 4 dangling image(s)")
            .And.Contain("panel: 3 image(s), 3 GB")
            .And.Contain("web-02: 1 image(s), 512 MB")
            .And.Contain("across 2 of 2 server(s)");
    }

    [Fact]
    public async Task A_v1_node_and_an_old_agent_are_named_as_not_examined_and_are_not_a_warning()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));
        var old = await AddServerAsync("web-06");
        _engines.On(old.Id, new Harbora.Infrastructure.Docker.RemoteDockerEngine(
            new StubAgentHandler.Factory(StubAgentHandler.OldAgent()), "http://web-06.example.com", "t"));

        await Sweeper().RunOnceAsync(default);

        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information,
            "a steady state — warning about it every day would bury the failures that matter");
        line.Message.Should().Contain("web-03: not examined")
            .And.Contain(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"))
            .And.Contain("web-06: not examined")
            .And.Contain("too old")
            .And.Contain("across 1 of 3 server(s)");
    }

    [Fact]
    public async Task A_prune_that_fails_on_one_machine_is_one_warning_line_and_the_others_are_still_pruned()
    {
        await AddServerAsync("panel", local: true);
        var failing = await AddServerAsync("web-05");
        _engines.On(failing.Id, new FakeDockerEngine { PruneDanglingThrows = new InvalidOperationException("boom") });
        _panel.SeedDangling(2, 100);

        var result = await Sweeper().RunOnceAsync(default);

        result!.Removed.Should().Be(2);
        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning);
        line.Message.Should().Contain("web-05: failed").And.Contain("boom").And.Contain("failed on 1 server(s)");
    }

    // ---- nothing here can stop the host ----

    [Fact]
    public async Task A_run_that_throws_is_logged_and_never_thrown()
    {
        var sweeper = Sweeper(scopes: new ThrowingScopeFactory());

        var result = await sweeper.RunOnceAsync(default);

        result.Should().BeNull();
        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Error);
        line.Message.Should().StartWith(DanglingImageSweeper.LogPrefix).And.Contain("scope factory is down");
    }

    [Fact]
    public async Task A_shutdown_during_a_run_is_neither_a_failure_nor_a_line()
    {
        await AddServerAsync("panel", local: true);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        var result = await Sweeper().RunOnceAsync(stopping.Token);

        result.Should().BeNull();
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_first_run_does_not_stop_the_host_and_the_next_run_still_happens()
    {
        await AddServerAsync("panel", local: true);
        _panel.SeedDangling(2, 100);
        var scopes = new FlakyScopeFactory(Provider().GetRequiredService<IServiceScopeFactory>(), failFirst: 1);
        var sweeper = Sweeper(scopes, interval: TimeSpan.FromMilliseconds(30));

        await sweeper.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => _log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("pruned 2")));

            sweeper.ExecuteTask.Should().NotBeNull();
            sweeper.ExecuteTask!.IsFaulted.Should().BeFalse("an exception escaping a BackgroundService stops the host");
            sweeper.ExecuteTask.IsCompleted.Should().BeFalse("the loop is still running for its next interval");
            _log.Entries.Should().Contain(e => e.Level == LogLevel.Error, "the first run's failure was logged");
        }
        finally
        {
            await sweeper.StopAsync(default);
        }

        // Completed and not faulted — not "ran to completion": on .NET 10 a BackgroundService stopped while it
        // waits reports Canceled, which is the host stopping, not a failure.
        sweeper.ExecuteTask!.IsCompleted.Should().BeTrue("stopping the host ends the loop");
        sweeper.ExecuteTask.IsFaulted.Should().BeFalse("and it ends without an exception escaping into the host");
    }

    [Fact]
    public async Task The_loop_runs_again_on_every_interval()
    {
        await AddServerAsync("panel", local: true);
        var sweeper = Sweeper(interval: TimeSpan.FromMilliseconds(30));

        await sweeper.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => _log.Entries.Count(e => e.Message.StartsWith(DanglingImageSweeper.LogPrefix)) >= 3);
        }
        finally
        {
            await sweeper.StopAsync(default);
        }

        _panel.CountOf(nameof(IDockerEngine.PruneDanglingImagesAsync)).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task An_interval_of_zero_turns_it_off_says_so_and_never_looks_at_a_server()
    {
        var sweeper = Sweeper(
            scopes: new ThrowingScopeFactory(),
            options: new HarboraRuntimeOptions { DanglingImageSweepHours = 0 });

        await sweeper.StartAsync(default);
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await sweeper.StopAsync(default);

        sweeper.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().Contain("off").And.Contain("Runtime:DanglingImageSweepHours");
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_does_not_stop_the_host()
    {
        // "Runtime__DanglingImageSweepHours=daily" — IOptions<T>.Value is where binding it throws, and
        // that throw happens synchronously inside the host's StartAsync.
        var sweeper = new DanglingImageSweeper(
            Provider().GetRequiredService<IServiceScopeFactory>(), new UnreadableOptions(), _log,
            TimeSpan.Zero, intervalOverride: null);

        var start = () => sweeper.StartAsync(default);
        await start.Should().NotThrowAsync();
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await sweeper.StopAsync(default);

        sweeper.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        _log.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error && e.Message.Contains("Runtime:DanglingImageSweepHours"));
    }

    [Fact]
    public async Task Stopping_the_host_during_the_startup_delay_returns_promptly()
    {
        var sweeper = Sweeper(startupDelay: TimeSpan.FromHours(1));

        await sweeper.StartAsync(default);
        var stop = sweeper.StopAsync(default);

        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        sweeper.ExecuteTask!.IsCompleted.Should().BeTrue();
        sweeper.ExecuteTask.IsFaulted.Should().BeFalse();
        _log.Entries.Should().BeEmpty("nothing ran, and nothing is logged for a run that never started");
    }

    [Fact]
    public void The_first_pass_is_soon_after_boot_not_a_full_interval_away()
    {
        // A panel that restarts at least daily (it redeploys itself, the host reboots) would otherwise
        // restart its own timer every time and never sweep. And it is not immediate: the reconcilers
        // are competing for the daemon on the boot path.
        DanglingImageSweeper.DefaultStartupDelay.Should().BeGreaterThan(TimeSpan.FromMinutes(1));
        DanglingImageSweeper.DefaultStartupDelay.Should().BeLessThan(TimeSpan.FromHours(1));
    }

    // ---- the setting ----

    [Fact]
    public void The_default_is_once_a_day()
    {
        new HarboraRuntimeOptions().DanglingImageSweepHours.Should().Be(24);
        new HarboraRuntimeOptions().DanglingImageSweepInterval.Should().Be(TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void Zero_negative_and_not_a_number_all_mean_off(double hours)
    {
        new HarboraRuntimeOptions { DanglingImageSweepHours = hours }.DanglingImageSweepInterval.Should().BeNull();
    }

    [Theory]
    [InlineData(0.5, 30)]
    [InlineData(1, 60)]
    [InlineData(6, 360)]
    [InlineData(168, 168 * 60)]
    public void An_ordinary_value_is_that_many_hours(double hours, double minutes)
    {
        new HarboraRuntimeOptions { DanglingImageSweepHours = hours }.DanglingImageSweepInterval
            .Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void A_value_below_a_minute_is_raised_to_a_minute_rather_than_pruning_in_a_tight_loop()
    {
        new HarboraRuntimeOptions { DanglingImageSweepHours = 0.000001 }.DanglingImageSweepInterval
            .Should().Be(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(1e12)]
    [InlineData(double.PositiveInfinity)]
    public async Task A_value_too_large_for_a_timer_is_clamped_and_cannot_throw_while_the_panel_starts(double hours)
    {
        var interval = new HarboraRuntimeOptions { DanglingImageSweepHours = hours }.DanglingImageSweepInterval;

        interval.Should().Be(TimeSpan.FromDays(30));

        // The delay the loop actually awaits must accept it: Task.Delay refuses a period past ~49 days
        // with an ArgumentOutOfRangeException, which is the throw that would take the host down. With a
        // dead token it returns a cancelled task — but only if the argument was accepted first.
        await FluentActions.Awaiting(() => Task.Delay(interval!.Value, new CancellationToken(canceled: true)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- helpers ----

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the sweeper never reached the expected state");
            await Task.Delay(10);
        }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("scope factory is down");
    }

    /// <summary>Fails the first <paramref name="failFirst"/> scopes and then behaves — a run that throws,
    /// followed by the next interval's run.</summary>
    private sealed class FlakyScopeFactory(IServiceScopeFactory inner, int failFirst) : IServiceScopeFactory
    {
        private int _calls;

        public IServiceScope CreateScope() =>
            Interlocked.Increment(ref _calls) <= failFirst
                ? throw new InvalidOperationException("scope factory is down")
                : inner.CreateScope();
    }

    private sealed class UnreadableOptions : IOptions<HarboraRuntimeOptions>
    {
        public HarboraRuntimeOptions Value =>
            throw new InvalidOperationException("Failed to convert configuration value 'daily' to type 'System.Double'.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message);

        private readonly ConcurrentQueue<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries => _entries.ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }
}
