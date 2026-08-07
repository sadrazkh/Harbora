using System.Reflection;
using Harbora.Cli;
using Spectre.Console.Cli;

// An update renames the old binary aside rather than overwriting a running file; this is where the
// leftover goes away.
if (Environment.ProcessPath is { Length: > 0 } self) SelfUpdate.CleanUpPreviousBinary(self);

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("harbora");
    // `harbora --version` is table stakes for a released CLI and the first thing a bug report needs.
    config.SetApplicationVersion(
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0");
    config.AddCommand<InitCommand>("init").WithDescription("Create a harbora.yml in the current folder.");
    config.AddCommand<LoginCommand>("login").WithDescription("Authenticate against a Harbora server.");
    config.AddCommand<WhoAmICommand>("whoami").WithDescription("Show the authenticated user.");
    config.AddCommand<AccountsCommand>("accounts").WithDescription("List signed-in accounts, switch, or log out.");
    config.AddCommand<StatusCommand>("status").WithDescription("Check server/session status.");
    config.AddCommand<AppsCommand>("apps").WithDescription("List applications.");
    config.AddCommand<DeployCommand>("deploy").WithDescription("Deploy an app and follow the logs.");
    config.AddCommand<LogsCommand>("logs").WithDescription("Stream logs for a deployment.");
    config.AddCommand<CancelCommand>("cancel").WithDescription("Stop a queued or running deployment.");
    config.AddCommand<UpdateCommand>("update").WithDescription("Update this CLI to the latest release.");

#if DEBUG
    config.PropagateExceptions();
#endif
});

return await app.RunAsync(args);
