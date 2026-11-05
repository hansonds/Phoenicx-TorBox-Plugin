using MediaBrowser.Model.Tasks;
using PhoenicxTorBox.Library;

namespace PhoenicxTorBox.Tasks;

/// <summary>Dashboard → Scheduled Tasks → "Sync TorBox library". Runs at startup and every 3 hours by default.</summary>
public class SyncTask : IScheduledTask
{
    private readonly LibrarySync _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTask"/> class.
    /// </summary>
    public SyncTask(LibrarySync sync)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public string Name => "Sync TorBox library";

    /// <inheritdoc />
    public string Key => "PhoenicxTorBoxSync";

    /// <inheritdoc />
    public string Description => "Reads your TorBox account and updates the TorBox movie and series libraries.";

    /// <inheritdoc />
    public string Category => "Torvia";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await _sync.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => TriggersFor(Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 180);

    /// <summary>Startup sync, plus every N minutes (none when N is 0).</summary>
    public static List<TaskTriggerInfo> TriggersFor(int minutes)
    {
        var list = new List<TaskTriggerInfo> { new() { Type = TaskTriggerInfoType.StartupTrigger } };
        if (minutes > 0)
        {
            list.Add(new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(Math.Max(15, minutes)).Ticks,
            });
        }

        return list;
    }
}
