using System.Net.Sockets;
using FluentAssertions;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules the job worker applies to work it did not write: how long it may run, how often it may
/// be tried again, and which failures are worth trying again at all.
///
/// The queue used to have no answer to any of the three. A <c>docker build</c> that hung against a
/// live daemon ran for ever, and because the worker is serial it took every other tenant's
/// background work with it. <c>Job.Attempts</c> was counted on every claim and read by nothing.
/// </summary>
public class JobExecutionPolicyTests
{
    [Fact]
    public void Every_kind_of_work_has_a_deadline()
    {
        // A kind added to the enum without a thought about its deadline still gets one — a missing
        // entry must not mean "run for ever", which is the defect this class exists to remove.
        foreach (var kind in Enum.GetValues<JobKind>())
            JobExecutionPolicy.TimeoutFor(kind).Should().BeGreaterThan(TimeSpan.Zero, $"{kind} must be bounded");
    }

    [Fact]
    public void A_deadline_sits_above_the_bound_the_work_imposes_on_itself()
    {
        // Where the work already limits itself, the queue's deadline is the backstop behind that
        // limit, never a competing one: the inner timeout says something specific ("the job was
        // still running after 1 hour"), and it can only say it if it fires first.
        JobExecutionPolicy.TimeoutFor(JobKind.CronRun).Should().BeGreaterThan(
            Harbora.Infrastructure.Deployments.CronJobRunner.MaxRunTime);

        JobExecutionPolicy.TimeoutFor(JobKind.BackupSnapshot).Should().BeGreaterThan(TimeSpan.FromHours(6),
            "the backup module allows a single Kopia command six hours");
        JobExecutionPolicy.TimeoutFor(JobKind.BackupRestore).Should().BeGreaterThan(TimeSpan.FromHours(6));
    }

    [Theory]
    [InlineData(JobKind.Deployment, 60)]
    [InlineData(JobKind.ServiceProvision, 15)]
    [InlineData(JobKind.CronRun, 75)]
    [InlineData(JobKind.Backup, 360)]
    [InlineData(JobKind.BackupSnapshot, 420)]
    [InlineData(JobKind.BackupRestore, 420)]
    [InlineData(JobKind.BackupVerify, 60)]
    [InlineData(JobKind.BackupPrune, 60)]
    [InlineData(JobKind.RepositoryHealthCheck, 5)]
    public void Each_kind_gets_the_deadline_its_work_needs(JobKind kind, int minutes)
        => JobExecutionPolicy.TimeoutFor(kind).Should().Be(TimeSpan.FromMinutes(minutes));

    [Fact]
    public void A_deployment_is_never_run_a_second_time_on_its_own()
    {
        // Half of a deployment may already have happened — an image pushed, a container started, the
        // proxy repointed. Re-running it blind could compound exactly the damage the operator is
        // trying to understand.
        JobExecutionPolicy.MaxAttemptsFor(JobKind.Deployment).Should().Be(1);
    }

    [Fact]
    public void A_restore_is_never_run_a_second_time_on_its_own()
    {
        // A restore writes over live data. One unattended repeat is one too many.
        JobExecutionPolicy.MaxAttemptsFor(JobKind.BackupRestore).Should().Be(1);
    }

    [Theory]
    [InlineData(JobKind.ServiceProvision, 3)]
    [InlineData(JobKind.RepositoryHealthCheck, 3)]
    [InlineData(JobKind.Backup, 2)]
    [InlineData(JobKind.BackupSnapshot, 2)]
    [InlineData(JobKind.BackupVerify, 2)]
    [InlineData(JobKind.BackupPrune, 2)]
    [InlineData(JobKind.CronRun, 1)]
    public void Repeatable_work_gets_a_budget_of_attempts(JobKind kind, int attempts)
        => JobExecutionPolicy.MaxAttemptsFor(kind).Should().Be(attempts);

    [Fact]
    public void Every_kind_may_be_attempted_at_least_once()
        => Enum.GetValues<JobKind>().Should().OnlyContain(k => JobExecutionPolicy.MaxAttemptsFor(k) >= 1);

    [Fact]
    public void A_broken_connection_is_worth_trying_again()
    {
        // The failures this exists for: the daemon was restarting, the object store hiccupped, a
        // socket died mid-upload. Nothing about the work itself was wrong.
        JobExecutionPolicy.IsRetryable(new HttpRequestException("connection refused")).Should().BeTrue();
        JobExecutionPolicy.IsRetryable(new SocketException(10061)).Should().BeTrue();
        JobExecutionPolicy.IsRetryable(new IOException("the pipe was closed")).Should().BeTrue();
        JobExecutionPolicy.IsRetryable(new TimeoutException("the registry did not answer")).Should().BeTrue();
    }

    [Fact]
    public void A_fault_in_the_work_itself_is_not()
    {
        // A bad Dockerfile fails identically every time. Retrying it wastes the queue and buries the
        // real message under three copies of itself.
        JobExecutionPolicy.IsRetryable(new InvalidOperationException("the build exploded")).Should().BeFalse();
        JobExecutionPolicy.IsRetryable(new ArgumentException("no such app")).Should().BeFalse();
    }

    [Fact]
    public void A_deadline_the_worker_imposed_is_not_worth_trying_again()
    {
        // This is the one that matters. A job killed at its deadline did not fail transiently — it
        // spent its whole allowance and finished nothing. Running it again just spends it again,
        // and a hung docker build hangs exactly as long the second time.
        JobExecutionPolicy.IsRetryable(new OperationCanceledException()).Should().BeFalse();
        JobExecutionPolicy.IsRetryable(new TaskCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void The_wait_between_attempts_grows_and_then_holds()
    {
        // Long enough that a restarting daemon has come back, short enough that a user watching the
        // page sees it happen. It stops growing because a job that has failed three times is not
        // going to be fixed by waiting longer — an operator is.
        JobExecutionPolicy.BackoffFor(1).Should().Be(TimeSpan.FromMinutes(1));
        JobExecutionPolicy.BackoffFor(2).Should().Be(TimeSpan.FromMinutes(5));
        JobExecutionPolicy.BackoffFor(3).Should().Be(TimeSpan.FromMinutes(30));
        JobExecutionPolicy.BackoffFor(4).Should().Be(TimeSpan.FromMinutes(30));
        JobExecutionPolicy.BackoffFor(40).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void A_backoff_asked_for_before_the_first_attempt_is_still_a_wait()
    {
        // Defensive: the caller passes Job.Attempts, which is 1 by the time a job can fail. A zero
        // would otherwise mean "claim it again immediately" and spin the worker.
        JobExecutionPolicy.BackoffFor(0).Should().Be(TimeSpan.FromMinutes(1));
        JobExecutionPolicy.BackoffFor(-1).Should().Be(TimeSpan.FromMinutes(1));
    }
}
