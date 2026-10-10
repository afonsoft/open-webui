using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Retenção de dados transientes (SPEC-20261010-memory-autosync-retention):
/// expurgo diário de registros operacionais velhos (AutomationRuns,
/// Notifications, steers de runs terminadas), poda de worktrees órfãs via
/// <see cref="WorktreeService.PruneOrphansAsync"/>, checkpoint do WAL a cada
/// tick e VACUUM semanal no SQLite. Nunca toca em dados primários do usuário
/// (chats, mensagens, arquivos, memórias).
/// </summary>
public class RetentionService(
    AppDbContext db, ConfigService config, WorktreeService worktrees,
    ILogger<RetentionService> logger)
{
    /// <summary>Dias de retenção das execuções de automações.</summary>
    public const int AutomationRunDays = 90;

    /// <summary>Dias de retenção das notificações.</summary>
    public const int NotificationDays = 60;

    /// <summary>Dias de retenção de steers de runs terminadas.</summary>
    public const int SteerDays = 30;

    /// <summary>Dias mínimos entre VACUUMs.</summary>
    public const int VacuumDays = 7;

    private const int Day = 24 * 3600;

    /// <summary>Executa um ciclo completo de retenção.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var runs = await db.AutomationRuns
            .Where(r => r.FinishedAt < now - AutomationRunDays * Day)
            .ExecuteDeleteAsync(ct);
        var notifications = await db.Notifications
            .Where(n => n.CreatedAt < now - NotificationDays * Day)
            .ExecuteDeleteAsync(ct);
        var terminalRunIds = db.ChatRuns
            .Where(r => r.Status == ChatRunStatus.Completed
                || r.Status == ChatRunStatus.Failed
                || r.Status == ChatRunStatus.Stopped
                || r.Status == ChatRunStatus.Interrupted)
            .Select(r => r.Id);
        var steers = await db.ChatRunSteers
            .Where(s => s.Timestamp < now - SteerDays * Day
                && terminalRunIds.Contains(s.RunId))
            .ExecuteDeleteAsync(ct);

        if (runs + notifications + steers > 0)
        {
            logger.LogInformation(
                "Retenção: {Runs} automation-runs, {Notifications} notificações, "
                    + "{Steers} steers expurgados.",
                runs, notifications, steers);
        }

        await worktrees.PruneOrphansAsync(ct);
        await CheckpointAsync(ct);
        await MaybeVacuumAsync(now, ct);
    }

    /// <summary>Checkpoint do WAL a cada tick — mantém o -wal pequeno.</summary>
    private async Task CheckpointAsync(CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "PRAGMA wal_checkpoint(TRUNCATE)", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "wal_checkpoint falhou.");
        }
    }

    /// <summary>VACUUM semanal — kv <c>sys:vacuum.last</c> guarda o último.</summary>
    private async Task MaybeVacuumAsync(long now, CancellationToken ct)
    {
        var last = await config.GetAsync("sys:vacuum.last", 0L, ct);
        if (now - last < VacuumDays * Day)
        {
            return;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
            await config.SetAsync("sys:vacuum.last", now, ct);
            logger.LogInformation("VACUUM executado pela retenção.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Base ocupada — tenta no próximo tick, sem derrubar o serviço.
            logger.LogWarning(ex, "VACUUM falhou.");
        }
    }
}

/// <summary>BackgroundService que roda a retenção uma vez por dia.</summary>
public class RetentionScheduler(
    IServiceScopeFactory scopeFactory, ILogger<RetentionScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
                await service.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tick da retenção falhou.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
