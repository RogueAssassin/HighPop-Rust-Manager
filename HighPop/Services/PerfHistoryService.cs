using System.IO;
using System.Text.Json;

namespace HighPop.Services;

public record PerfSnapshot(
    DateTime Time,
    double Cpu,
    long MemMb,
    double NetworkInKbps = 0,
    double NetworkOutKbps = 0,
    int Players = 0);

public class PerfHistoryService
{
    private readonly Dictionary<string, Queue<PerfSnapshot>> _history = new();
    private readonly Dictionary<string, int> _samplesSinceSave = new();
    private readonly object _lock = new();
    private readonly string _dir;

    // 1 sample every ~2 s → 1800 samples ≈ 1 hour of history in memory
    private const int MaxSamples = 1800;

    public PerfHistoryService(ConfigService config)
    {
        _dir = Path.Combine(config.AppDataPath, "perf_history");
        Directory.CreateDirectory(_dir);
    }

    public void Record(
        string serverId,
        double cpu,
        long memMb,
        double networkInKbps = 0,
        double networkOutKbps = 0,
        int players = 0)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(serverId, out var q))
            {
                q = LoadFromDisk(serverId);
                _history[serverId] = q;
            }

            q.Enqueue(new PerfSnapshot(
                DateTime.Now,
                cpu,
                memMb,
                Math.Max(0, networkInKbps),
                Math.Max(0, networkOutKbps),
                Math.Max(0, players)));
            while (q.Count > MaxSamples) q.Dequeue();

            // Persist every 60 new samples (~2 min). Using q.Count here would write on
            // every sample once the fixed-size queue reaches 1,800 entries.
            var pending = _samplesSinceSave.GetValueOrDefault(serverId) + 1;
            _samplesSinceSave[serverId] = pending;
            if (pending >= 60)
            {
                SaveToDisk(serverId, q);
                _samplesSinceSave[serverId] = 0;
            }
        }
    }

    public IReadOnlyCollection<PerfSnapshot> Get(string serverId)
    {
        lock (_lock)
        {
            if (!_history.TryGetValue(serverId, out var q))
            {
                q = LoadFromDisk(serverId);
                _history[serverId] = q;
            }
            return q.ToList();
        }
    }

    public void Flush(string serverId)
    {
        lock (_lock)
        {
            if (_history.TryGetValue(serverId, out var q))
            {
                SaveToDisk(serverId, q);
                _samplesSinceSave[serverId] = 0;
            }
        }
    }

    public void Clear(string serverId)
    {
        lock (_lock)
        {
            _history.Remove(serverId);
            _samplesSinceSave.Remove(serverId);
            var f = FilePath(serverId);
            if (File.Exists(f)) File.Delete(f);
        }
    }

    private string FilePath(string serverId)
        => Path.Combine(_dir, $"{serverId}.json");

    private Queue<PerfSnapshot> LoadFromDisk(string serverId)
    {
        var q = new Queue<PerfSnapshot>(MaxSamples + 1);
        var f = FilePath(serverId);
        if (!File.Exists(f)) return q;
        try
        {
            var list = JsonSerializer.Deserialize<List<PerfSnapshot>>(File.ReadAllText(f));
            if (list != null)
                foreach (var s in list.TakeLast(MaxSamples))
                    q.Enqueue(s);
        }
        catch { /* corrupt → start fresh */ }
        return q;
    }

    private void SaveToDisk(string serverId, Queue<PerfSnapshot> q)
    {
        try { File.WriteAllText(FilePath(serverId), JsonSerializer.Serialize(q.ToList())); }
        catch { }
    }
}
