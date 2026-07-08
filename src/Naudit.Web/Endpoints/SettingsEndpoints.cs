using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;

namespace Naudit.Web.Endpoints;

/// <summary>Editierbare Settings (Admin): GET zeigt Katalog + Quelle (db/env/default), PUT schreibt
/// in die DB (Secrets write-only), POST restart übernimmt per Host-Neustart. Env-gesetzte Keys
/// sind gesperrt — env gewinnt immer über DB, die UI macht das sichtbar statt verwirrend.</summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();

        group.MapGet("/", async (HttpContext ctx, NauditDbContext db, SettingsService settings,
            IConfiguration config, EnvOverrides env, IAppRestarter restarter, StartupState startup) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            var dbKeys = await settings.GetSetKeysAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                recoveryError = startup.RecoveryError,
                warnings = startup.Warnings,
                restartPending = restarter.RestartPending,
                settings = SettingsCatalog.All.Select(def =>
                {
                    var envLocked = env.Root[def.Key] is not null;
                    var isSet = envLocked || dbKeys.Contains(def.Key) || config[def.Key] is not null;
                    return new
                    {
                        key = def.Key,
                        isSecret = def.IsSecret,
                        isSet,
                        source = envLocked ? "env" : dbKeys.Contains(def.Key) ? "db" : "default",
                        editable = !envLocked,
                        value = def.IsSecret ? null : config[def.Key],
                    };
                }),
            });
        });

        group.MapPut("/", async (HttpContext ctx, NauditDbContext db, SettingsService settings,
            EnvOverrides env, IAppRestarter restarter, UpdateSettingsRequest body) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            // Erst komplett validieren, dann schreiben — keine halb angewendeten Batches.
            foreach (var change in body.Changes)
            {
                if (!SettingsCatalog.TryGet(change.Key, out _))
                    return Results.BadRequest(new { error = $"'{change.Key}' is not a managed setting." });
                if (env.Root[change.Key] is not null)
                    return Results.BadRequest(new { error = $"'{change.Key}' is set via environment and cannot be edited here." });
            }
            foreach (var change in body.Changes)
            {
                if (change.Value is null) await settings.RemoveAsync(change.Key, ctx.RequestAborted);
                else await settings.SetAsync(change.Key, change.Value, ctx.RequestAborted);
            }
            restarter.MarkRestartPending();
            return Results.Ok(new { restartPending = true });
        });

        group.MapPost("/restart", async (HttpContext ctx, NauditDbContext db, IAppRestarter restarter) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            restarter.RequestRestart();
            return Results.NoContent();
        });
    }
}

public sealed record SettingChange(string Key, string? Value);
public sealed record UpdateSettingsRequest(List<SettingChange> Changes);
