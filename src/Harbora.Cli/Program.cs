using Harbora.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("harbora");
    config.AddCommand<InitCommand>("init").WithDescription("Create a harbora.yml in the current folder.");
    config.AddCommand<LoginCommand>("login").WithDescription("Authenticate against a Harbora server.");
    config.AddCommand<WhoAmICommand>("whoami").WithDescription("Show the authenticated user.");
    config.AddCommand<StatusCommand>("status").WithDescription("Check server/session status.");
    config.AddCommand<AppsCommand>("apps").WithDescription("List applications.");
    config.AddCommand<DeployCommand>("deploy").WithDescription("Deploy an app and follow the logs.");
    config.AddCommand<LogsCommand>("logs").WithDescription("Stream logs for a deployment.");

#if DEBUG
    config.PropagateExceptions();
#endif
});

return await app.RunAsync(args);
