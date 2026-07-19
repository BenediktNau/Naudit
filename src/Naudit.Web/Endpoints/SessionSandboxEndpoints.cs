using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Sandbox-Status fürs SPA (Statuszeile auf der Profilseite). Nur gemappt, wenn
/// SessionSandbox=Docker — sonst existiert die Route nicht (404) und das SPA zeigt nichts
/// (Muster GitHubAppEndpoints).</summary>
public static class SessionSandboxEndpoints
{
    public static void MapSessionSandboxEndpoints(this WebApplication app, AiOptions ai)
    {
        if (ai.SessionSandbox != SessionSandbox.Docker)
            return;

        app.MapGet("/api/me/session-sandbox",
            async (HttpContext ctx, NauditDbContext db, SessionSandboxState state, SessionContainerManager manager) =>
            {
                var acct = await CurrentAccount.GetAsync(ctx, db);
                if (acct is null) return Results.Unauthorized();

                int? live = null;
                try
                {
                    live = await manager.CountRunningAsync(ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Socket weg ⇒ null; socketReachable erzählt dem SPA den Rest.
                }
                return Results.Ok(new
                {
                    mode = "Docker",
                    socketReachable = state.SocketReachable,
                    liveContainers = live,
                });
            }).RequireAuthorization();
    }
}
