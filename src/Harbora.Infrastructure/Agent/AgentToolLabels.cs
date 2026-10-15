namespace Harbora.Infrastructure.Agent;

/// <summary>A tool's name for people: what the pane shows instead of its identifier, and its icon's group.</summary>
/// <param name="En">An activity, in the present — "Reading the deployment's logs" — so the same words
/// serve the running line ("…") and the finished one.</param>
/// <param name="Category">One of <see cref="AgentToolLabels.Categories"/>.</param>
public sealed record AgentToolLabel(string En, string Fa, string Category);

/// <summary>
/// The names people read for the agent's tools. <c>get_deployment</c> is the model's word; "Reading the
/// deployment's logs" / «خواندن لاگ‌های استقرار» is the person's. Every registered tool has an entry — a
/// test holds the registry to it — and a tool without one falls back to its identifier under
/// <see cref="Categories.Other"/> rather than to nothing.
/// </summary>
public static class AgentToolLabels
{
    /// <summary>The icon groups. The pane maps each to one icon; a value it does not know gets the generic one.</summary>
    public static class Categories
    {
        public const string App = "app";
        public const string Deployment = "deployment";
        public const string Database = "database";
        public const string Project = "project";
        public const string Domain = "domain";
        public const string Volume = "volume";
        public const string Env = "env";
        public const string Catalog = "catalog";
        public const string Usage = "usage";
        public const string Resources = "resources";
        public const string Other = "other";
    }

    /// <summary>
    /// Tool name → label. Keyed by <see cref="IAgentTool.Name"/>, ordinal. Each label is an activity, so
    /// the same words read as "Creating a database…" while it runs and as the name of the finished line.
    /// Persian is the panel's default voice: a verbal noun the pane can follow with «در حال» or leave
    /// as it is. The project and database tools' words are the ones their plan cards already use.
    /// </summary>
    public static IReadOnlyDictionary<string, AgentToolLabel> All { get; } = new Dictionary<string, AgentToolLabel>(StringComparer.Ordinal)
    {
        // Reads
        ["list_resources"] = new("Looking through your projects and resources", "مرور پروژه‌ها و منابع", Categories.Resources),
        ["get_app"] = new("Reading an app's details", "خواندن جزئیات اپ", Categories.App),
        ["get_database"] = new("Reading a database's details", "خواندن جزئیات دیتابیس", Categories.Database),
        ["get_catalog"] = new("Browsing what can be created", "مرور آنچه می‌توان ساخت", Categories.Catalog),
        ["get_deployment"] = new("Reading the deployment's logs", "خواندن لاگ‌های استقرار", Categories.Deployment),
        ["get_usage"] = new("Checking your plan's usage and limits", "بررسی مصرف و سقف‌های پلن", Categories.Usage),

        // Apps
        ["create_app"] = new("Creating an app", "ساخت اپ", Categories.App),
        ["deploy_template"] = new("Deploying a template as a new project", "استقرار قالب به‌عنوان پروژهٔ جدید", Categories.Catalog),
        ["set_env_vars"] = new("Setting environment variables", "تنظیم متغیرهای محیطی", Categories.Env),
        ["delete_env_var"] = new("Deleting an environment variable", "حذف متغیر محیطی", Categories.Env),
        ["add_domain"] = new("Adding a domain", "افزودن دامنه", Categories.Domain),
        ["remove_domain"] = new("Removing a domain", "حذف دامنه", Categories.Domain),
        ["add_volume"] = new("Adding a volume", "افزودن والیوم", Categories.Volume),
        ["remove_volume"] = new("Removing a volume", "حذف والیوم", Categories.Volume),
        ["deploy_app"] = new("Deploying an app", "استقرار اپ", Categories.Deployment),
        ["restart_app"] = new("Restarting an app", "راه‌اندازی دوبارهٔ اپ", Categories.App),
        ["stop_app"] = new("Stopping an app", "توقف اپ", Categories.App),
        ["rollback_app"] = new("Rolling an app back to an earlier deployment", "بازگردانی اپ به استقرار قبلی", Categories.Deployment),
        ["delete_app"] = new("Deleting an app", "حذف اپ", Categories.App),

        // Projects and databases
        ["create_project"] = new("Creating a project", "ساخت پروژه", Categories.Project),
        ["add_environment"] = new("Adding an environment to a project", "افزودن محیط به پروژه", Categories.Project),
        ["create_database"] = new("Creating a database", "ساخت دیتابیس", Categories.Database),
        ["attach_database"] = new("Attaching a database to an app", "اتصال دیتابیس به اپ", Categories.Database),
        ["detach_database"] = new("Detaching a database from an app", "جدا کردن دیتابیس از اپ", Categories.Database),
        ["rotate_database_password"] = new("Changing a database's password", "تعویض رمز دیتابیس", Categories.Database),
        ["stop_database"] = new("Stopping a database", "توقف دیتابیس", Categories.Database),
        ["start_database"] = new("Starting a database", "راه‌اندازی دیتابیس", Categories.Database),
        ["delete_database"] = new("Deleting a database", "حذف دیتابیس", Categories.Database),
        ["delete_project"] = new("Deleting a project", "حذف پروژه", Categories.Project)
    };

    public static AgentToolLabel For(string toolName) =>
        All.TryGetValue(toolName, out var label) ? label : new AgentToolLabel(toolName, toolName, Categories.Other);
}
