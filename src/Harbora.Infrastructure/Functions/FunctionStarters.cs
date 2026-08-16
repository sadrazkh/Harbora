using Harbora.Domain.Functions;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// What the editor is pre-filled with.
///
/// <para>
/// Not decoration. The generated host calls one exact entry point per language, and a person who has
/// to discover that entry point from a documentation page will get it wrong once and read a compile
/// error instead of a result. The starter is the contract, written out — it compiles, it runs, and
/// it is short enough to delete.
/// </para>
/// </summary>
public static class FunctionStarters
{
    public static string For(FunctionRuntime runtime, FunctionTrigger trigger) => (runtime, trigger) switch
    {
        (FunctionRuntime.CSharp, FunctionTrigger.Http) =>
            """
            public static class Function
            {
                public static Task<FnResponse> Run(FnRequest req, FnContext ctx)
                {
                    var name = req.Query.TryGetValue("name", out var n) ? n : "world";
                    return Task.FromResult(FnResponse.Json(new { hello = name, method = req.Method }));
                }
            }
            """,

        (FunctionRuntime.CSharp, FunctionTrigger.Cron) =>
            """
            public static class Function
            {
                public static Task<FnResponse> Run(FnRequest req, FnContext ctx)
                {
                    ctx.Log($"Ran at {DateTimeOffset.UtcNow:O}");
                    return Task.FromResult(FnResponse.Empty());
                }
            }
            """,

        (FunctionRuntime.CSharp, _) =>
            """
            public static class Function
            {
                public static Task<FnResponse> Run(FnRequest req, FnContext ctx)
                {
                    // ctx.Event is what happened. Its Data keys depend on the event you subscribed to.
                    ctx.Log($"{ctx.Event?.Key} on {ctx.Event?.Subject}");
                    return Task.FromResult(FnResponse.Empty());
                }
            }
            """,

        (FunctionRuntime.JavaScript, FunctionTrigger.Http) =>
            """
            export default async function (req, ctx) {
              const name = req.query.name || 'world';
              return { hello: name, method: req.method };
            }
            """,

        (FunctionRuntime.JavaScript, FunctionTrigger.Cron) =>
            """
            export default async function (req, ctx) {
              ctx.log('ran at', new Date().toISOString());
            }
            """,

        (FunctionRuntime.JavaScript, _) =>
            """
            export default async function (req, ctx) {
              // ctx.event is what happened. Its data keys depend on the event you subscribed to.
              ctx.log(ctx.event?.key, ctx.event?.subject);
            }
            """,

        (FunctionRuntime.Python, FunctionTrigger.Http) =>
            """
            def run(req, ctx):
                name = req['query'].get('name', 'world')
                return {'hello': name, 'method': req['method']}
            """,

        (FunctionRuntime.Python, FunctionTrigger.Cron) =>
            """
            import datetime


            def run(req, ctx):
                ctx['log']('ran at', datetime.datetime.utcnow().isoformat())
            """,

        _ =>
            """
            def run(req, ctx):
                # ctx['event'] is what happened. Its data keys depend on the event you subscribed to.
                event = ctx['event'] or {}
                ctx['log'](event.get('key'), event.get('subject'))
            """
    };

    /// <summary>The file extension shown beside the editor, and used by the syntax highlighter.</summary>
    public static string Extension(FunctionRuntime runtime) => runtime switch
    {
        FunctionRuntime.CSharp => "cs",
        FunctionRuntime.JavaScript => "mjs",
        _ => "py"
    };

    public static string Label(FunctionRuntime runtime) => runtime switch
    {
        FunctionRuntime.CSharp => "C#",
        FunctionRuntime.JavaScript => "JavaScript",
        _ => "Python"
    };
}
