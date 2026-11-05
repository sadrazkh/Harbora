namespace Harbora.Shared.Inspection;

/// <summary>A migration command, only where the tooling is unmistakable.</summary>
internal static class ReleaseCommands
{
    /// <summary>
    /// A release command runs inside the built image, so each of these also needs a stack whose image
    /// has the tool: a Laravel repository whose <c>package.json</c> makes it build as Node has no PHP
    /// to run <c>artisan</c> with, and a release that fails stops the whole deploy. A Dockerfile can
    /// hold anything, and a compose stack has no one image for it to run in.
    /// </summary>
    public static string? For(SourceTree app, DetectedStack stack)
    {
        if (stack is DetectedStack.Node or DetectedStack.Dockerfile &&
            app.HasFile("prisma/schema.prisma") && app.HasDirectory("prisma/migrations"))
            return "npx prisma migrate deploy";

        if (stack is DetectedStack.Php or DetectedStack.Dockerfile && app.HasFile("artisan"))
            return "php artisan migrate --force";

        if (stack is DetectedStack.Python or DetectedStack.Dockerfile && app.HasFile("alembic.ini"))
            return "alembic upgrade head";

        return null;
    }
}
