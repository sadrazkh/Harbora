namespace Harbora.Domain.Common;

/// <summary>Coarse RBAC roles. Fine-grained project permissions layer on top via WorkspaceMember.</summary>
public enum SystemRole
{
    Owner = 0,   // full control, created at first-run setup
    Admin = 1,   // manage everything except billing/owner transfer
    Member = 2,  // deploy/manage assigned projects
    Viewer = 3   // read-only
}

public enum WorkspaceRole
{
    Admin = 0,
    Member = 1,
    Viewer = 2
}

/// <summary>How an application is built/sourced.</summary>
public enum AppSourceType
{
    GitRepository = 0,
    Dockerfile = 1,
    DockerCompose = 2,
    PrebuiltImage = 3,
    StaticSite = 4,
    Template = 5
}

public enum AppStatus
{
    Created = 0,
    Deploying = 1,
    Running = 2,
    Stopped = 3,
    Failed = 4,
    Crashed = 5
}

public enum DeploymentStatus
{
    Queued = 0,
    Building = 1,
    Pushing = 2,
    Deploying = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    RolledBack = 7,
    // Appended (value 8) so existing persisted values stay stable. Sits logically between
    // Deploying and Succeeded: the new container is up and being health-probed before cutover.
    HealthChecking = 8
}

public enum DeploymentTrigger
{
    Manual = 0,
    GitPush = 1,
    GitTag = 2,
    Webhook = 3,
    Cli = 4,
    Rollback = 5,
    Schedule = 6
}

public enum GitProviderType
{
    GitHub = 0,
    GitLab = 1,
    Gitea = 2,
    Bitbucket = 3,
    Custom = 4
}

public enum LogStream
{
    Build = 0,
    Runtime = 1,
    System = 2
}

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}

public enum CertificateStatus
{
    Pending = 0,
    Issued = 1,
    Expired = 2,
    Failed = 3,
    Revoked = 4
}

public enum RouteType
{
    HostBased = 0,
    PathBased = 1,
    Redirect = 2
}

public enum ManagedServiceType
{
    PostgreSql = 0,
    MySql = 1,
    MariaDb = 2,
    Redis = 3,
    MongoDb = 4
}

public enum ServiceStatus
{
    Provisioning = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3
}

public enum BackupType
{
    Database = 0,
    Volume = 1,
    AppConfig = 2,
    FullPlatform = 3,
    Service = 4
}

public enum BackupStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Expired = 4
}

public enum BackupDestinationType
{
    Local = 0,
    S3 = 1
}

public enum AlertChannel
{
    Email = 0,
    Telegram = 1,
    Discord = 2,
    Webhook = 3
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>The trigger that fired a notification; matched against an Alert's opt-in flags.</summary>
public enum AlertEvent
{
    DeployFailed = 0,
    AppCrashed = 1,
    SslExpiring = 2,
    DiskWarning = 3,
    BackupFailed = 4,
    Test = 5
}

public enum ServerStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Degraded = 3
}

public enum TokenType
{
    Api = 0,
    Cli = 1
}
