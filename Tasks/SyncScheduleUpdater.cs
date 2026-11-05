using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Tasks;

/// <summary>
/// Keeps the "Sync TorBox library" schedule in step with the sync interval chosen on the plugin page.
/// </summary>
public sealed class SyncScheduleUpdater : IHostedService
{
    private readonly ITaskManager _tasks;
    private readonly ILogger<SyncScheduleUpdater> _log;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduleUpdater"/> class.
    /// </summary>
    public SyncScheduleUpdater(ITaskManager tasks, ILogger<SyncScheduleUpdater> log)
    {
        _tasks = tasks;
        _log = log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } p)
        {
            p.ConfigurationChanged += OnConfigChanged;
        }

        // Scheduled tasks are registered a little after plugins start: apply once they exist.
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                for (var i = 0; i < 60 && !token.IsCancellationRequested; i++)
                {
                    if (Apply())
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            },
            token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } p)
        {
            p.ConfigurationChanged -= OnConfigChanged;
        }

        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnConfigChanged(object? sender, BasePluginConfiguration e) => Apply();

    /// <summary>Sets the task's schedule. Returns false while the task is not registered yet.</summary>
    private bool Apply()
    {
        try
        {
            var worker = _tasks.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is SyncTask);
            if (worker is null)
            {
                return false;
            }

            var minutes = Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 180;
            var wanted = SyncTask.TriggersFor(minutes);
            var current = worker.Triggers;
            var same = current.Count == wanted.Count
                && current.Zip(wanted).All(x => x.First.Type == x.Second.Type && x.First.IntervalTicks == x.Second.IntervalTicks);
            if (!same)
            {
                worker.Triggers = wanted;
                _log.LogInformation("Torvia: sync schedule set to {Minutes} minutes (0 = startup only)", minutes);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Torvia: could not set the sync schedule");
            return true;
        }
    }
}
