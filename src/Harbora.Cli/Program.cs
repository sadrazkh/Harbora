using System.Reflection;
using Harbora.Cli;
using Spectre.Console.Cli;

// Windows consoles still default to a legacy code page, so every ✓ and ✗ this CLI prints arrives as
// a replacement character — including the tick on a successful deploy and every line of `doctor`,
// where the glyph IS the verdict. Set once, before anything writes: a status symbol nobody can read
// is a report that does not report.
//
// Wrapped because a redirected or closed stream (a pipe, CI, `harbora apps > out.txt`) throws here,
// and failing to start over an encoding preference would be worse than the mangled glyph it fixes.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

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
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Check this project for problems before deploying.");
    config.AddCommand<DeployCommand>("deploy").WithDescription("Deploy an app and follow the logs.");
    config.AddCommand<LogsCommand>("logs").WithDescription("Stream logs for a deployment.");
    config.AddCommand<CancelCommand>("cancel").WithDescription("Stop a queued or running deployment.");
    config.AddCommand<UpdateCommand>("update").WithDescription("Update this CLI to the latest release.");
    // 4.1 (2026-09-04 local-dev-parity plan): local-dev parity, the other half of `deploy` — running
    // the same effective environment locally instead of only ever on the platform.
    config.AddBranch("env", env =>
    {
        env.SetDescription("Work with an app's effective environment.");
        env.AddCommand<EnvPullCommand>("pull").WithDescription("Write an app's effective environment to .env.local.");
    });
    config.AddCommand<RunCommand>("run").WithDescription("Run a local command with an app's effective environment injected.");

#if DEBUG
    config.PropagateExceptions();
#endif
});

return await app.RunAsync(args);
