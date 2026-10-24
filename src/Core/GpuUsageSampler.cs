using System.Diagnostics;
using System.Globalization;

namespace GpuSwitcher;

public enum EngineBucket { Render, Compute, Decode, Encode, VideoProc, Copy, Other }

/// <summary>One process's activity on one adapter.</summary>
public sealed class ProcAdapterUsage
{
    public long Luid;
    public double Render, Compute, Decode, Encode, VideoProc, Copy;   // % of an engine over the last interval
    public long Dedicated, Shared;                                    // bytes
    public long WorkTicks;    // cumulative non-copy engine time since process start (100 ns)
    public double Busy => Render + Compute + Decode + Encode + VideoProc;
}

public sealed class AdapterUsage
{
    public long Luid;
    public double Render, Compute, Decode, Encode, VideoProc, Copy;   // busiest engine of each kind, %
    public long DedicatedUsed, SharedUsed;
}

public sealed class GpuSnapshot
{
    public DateTime Time;
    public Dictionary<int, Dictionary<long, ProcAdapterUsage>> ByPid = new();
    public Dictionary<long, AdapterUsage> ByAdapter = new();

    /// <summary>The adapter a process is really rendering on: most cumulative engine time, then most memory.</summary>
    public long? DominantAdapter(IEnumerable<int> pids, out bool usesGpu)
    {
        var sum = new Dictionary<long, (long work, long mem, double busy)>();
        foreach (var pid in pids)
        {
            if (!ByPid.TryGetValue(pid, out var per)) continue;
            foreach (var (luid, u) in per)
            {
                sum.TryGetValue(luid, out var s);
                sum[luid] = (s.work + u.WorkTicks, s.mem + u.Dedicated + u.Shared, s.busy + u.Busy);
            }
        }
        usesGpu = sum.Values.Any(s => s.work > 0 || s.mem > 4 << 20);
        if (!usesGpu) return null;
        return sum.OrderByDescending(kv => kv.Value.work).ThenByDescending(kv => kv.Value.mem).First().Key;
    }
}

/// <summary>Samples the "GPU Engine" / "GPU Process Memory" / "GPU Adapter Memory" counters — the same data Task Manager uses.</summary>
public sealed class GpuUsageSampler
{
    readonly PerformanceCounterCategory _engine = new("GPU Engine");
    readonly PerformanceCounterCategory _procMem = new("GPU Process Memory");
    readonly PerformanceCounterCategory _adapterMem = new("GPU Adapter Memory");
    Dictionary<string, long> _prevRunning = new();
    long _prevStamp;

    public GpuSnapshot Sample()
    {
        var snap = new GpuSnapshot { Time = DateTime.Now };
        long stamp = Stopwatch.GetTimestamp();
        double elapsed100ns = _prevStamp == 0 ? 0 : (stamp - _prevStamp) * 1e7 / Stopwatch.Frequency;

        var engineData = _engine.ReadCategory();
        var running = engineData["Running Time"];
        var nowRunning = new Dictionary<string, long>(running?.Count ?? 0);
        // per adapter, per physical engine: sum of all processes' utilisation
        var engineTotals = new Dictionary<(long luid, int eng), (EngineBucket b, double pct)>();

        if (running != null)
        {
            foreach (InstanceData inst in running.Values)
            {
                if (!TryParseEngine(inst.InstanceName, out int pid, out long luid, out int eng, out var bucket)) continue;
                long raw = inst.RawValue;
                nowRunning[inst.InstanceName] = raw;
                double pct = 0;
                if (elapsed100ns > 0 && _prevRunning.TryGetValue(inst.InstanceName, out var prev) && raw >= prev)
                    pct = Math.Min(100, (raw - prev) * 100.0 / elapsed100ns);

                var u = Get(snap, pid, luid);
                if (bucket != EngineBucket.Copy && bucket != EngineBucket.Other) u.WorkTicks += raw;
                Add(u, bucket, pct);

                var k = (luid, eng);
                engineTotals.TryGetValue(k, out var t);
                engineTotals[k] = (bucket, t.pct + pct);
            }
        }
        _prevRunning = nowRunning;
        _prevStamp = stamp;

        foreach (var ((luid, _), (bucket, pct)) in engineTotals)
        {
            var a = GetAdapter(snap, luid);
            double p = Math.Min(100, pct);
            switch (bucket)
            {
                case EngineBucket.Render: a.Render = Math.Max(a.Render, p); break;
                case EngineBucket.Compute: a.Compute = Math.Max(a.Compute, p); break;
                case EngineBucket.Decode: a.Decode = Math.Max(a.Decode, p); break;
                case EngineBucket.Encode: a.Encode = Math.Max(a.Encode, p); break;
                case EngineBucket.VideoProc: a.VideoProc = Math.Max(a.VideoProc, p); break;
                case EngineBucket.Copy: a.Copy = Math.Max(a.Copy, p); break;
            }
        }

        var mem = _procMem.ReadCategory();
        var ded = mem["Dedicated Usage"];
        var sh = mem["Shared Usage"];
        if (ded != null)
            foreach (InstanceData inst in ded.Values)
                if (TryParseProcMem(inst.InstanceName, out int pid, out long luid)) Get(snap, pid, luid).Dedicated = inst.RawValue;
        if (sh != null)
            foreach (InstanceData inst in sh.Values)
                if (TryParseProcMem(inst.InstanceName, out int pid, out long luid)) Get(snap, pid, luid).Shared = inst.RawValue;

        var amem = _adapterMem.ReadCategory();
        var aded = amem["Dedicated Usage"];
        var ash = amem["Shared Usage"];
        if (aded != null)
            foreach (InstanceData inst in aded.Values)
                if (TryParseLuid(inst.InstanceName, 0, out long luid, out _)) GetAdapter(snap, luid).DedicatedUsed += inst.RawValue;
        if (ash != null)
            foreach (InstanceData inst in ash.Values)
                if (TryParseLuid(inst.InstanceName, 0, out long luid, out _)) GetAdapter(snap, luid).SharedUsed += inst.RawValue;

        return snap;
    }

    static ProcAdapterUsage Get(GpuSnapshot s, int pid, long luid)
    {
        if (!s.ByPid.TryGetValue(pid, out var per)) s.ByPid[pid] = per = new();
        if (!per.TryGetValue(luid, out var u)) per[luid] = u = new ProcAdapterUsage { Luid = luid };
        return u;
    }

    static AdapterUsage GetAdapter(GpuSnapshot s, long luid)
    {
        if (!s.ByAdapter.TryGetValue(luid, out var a)) s.ByAdapter[luid] = a = new AdapterUsage { Luid = luid };
        return a;
    }

    static void Add(ProcAdapterUsage u, EngineBucket b, double pct)
    {
        switch (b)
        {
            case EngineBucket.Render: u.Render = Math.Min(100, u.Render + pct); break;
            case EngineBucket.Compute: u.Compute = Math.Min(100, u.Compute + pct); break;
            case EngineBucket.Decode: u.Decode = Math.Min(100, u.Decode + pct); break;
            case EngineBucket.Encode: u.Encode = Math.Min(100, u.Encode + pct); break;
            case EngineBucket.VideoProc: u.VideoProc = Math.Min(100, u.VideoProc + pct); break;
            case EngineBucket.Copy: u.Copy = Math.Min(100, u.Copy + pct); break;
        }
    }

    public static EngineBucket BucketOf(string engType)
    {
        var t = engType.ToLowerInvariant();
        if (t.Contains("copy")) return EngineBucket.Copy;
        if (t.Contains("decode") || t.Contains("codec")) return EngineBucket.Decode;
        if (t.Contains("encode")) return EngineBucket.Encode;
        if (t.Contains("videoprocessing") || t.Contains("video processing")) return EngineBucket.VideoProc;
        if (t.Contains("compute") || t.Contains("cuda")) return EngineBucket.Compute;
        if (t.Contains("3d") || t.Contains("graphics") || t.Contains("render") || t.Contains("gdi")) return EngineBucket.Render;
        return EngineBucket.Other;
    }

    // pid_14204_luid_0x00000000_0x000194BF_phys_0_eng_0_engtype_3D
    static bool TryParseEngine(string s, out int pid, out long luid, out int eng, out EngineBucket bucket)
    {
        pid = 0; luid = 0; eng = 0; bucket = EngineBucket.Other;
        if (!s.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int us = s.IndexOf('_', 4);
        if (us < 0 || !int.TryParse(s.AsSpan(4, us - 4), out pid)) return false;
        if (!TryParseLuid(s, us + 1, out luid, out int after)) return false;
        int e = s.IndexOf("_eng_", after, StringComparison.Ordinal);
        int t = s.IndexOf("_engtype_", after, StringComparison.Ordinal);
        if (e < 0 || t < 0) return false;
        int.TryParse(s.AsSpan(e + 5, t - e - 5), out eng);
        bucket = BucketOf(s[(t + 9)..]);
        return true;
    }

    // pid_2156_luid_0x00000000_0x000194BF_phys_0
    static bool TryParseProcMem(string s, out int pid, out long luid)
    {
        pid = 0; luid = 0;
        if (!s.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int us = s.IndexOf('_', 4);
        if (us < 0 || !int.TryParse(s.AsSpan(4, us - 4), out pid)) return false;
        return TryParseLuid(s, us + 1, out luid, out _);
    }

    // luid_0x00000000_0x000194BF  (starting at index)
    static bool TryParseLuid(string s, int start, out long luid, out int end)
    {
        luid = 0; end = start;
        if (string.CompareOrdinal(s, start, "luid_0x", 0, 7) != 0) return false;
        int hiStart = start + 7;
        int hiEnd = s.IndexOf('_', hiStart);
        if (hiEnd < 0 || string.CompareOrdinal(s, hiEnd, "_0x", 0, 3) != 0) return false;
        int loStart = hiEnd + 3;
        int loEnd = s.IndexOf('_', loStart);
        if (loEnd < 0) loEnd = s.Length;
        if (!uint.TryParse(s.AsSpan(hiStart, hiEnd - hiStart), NumberStyles.HexNumber, null, out var hi)) return false;
        if (!uint.TryParse(s.AsSpan(loStart, loEnd - loStart), NumberStyles.HexNumber, null, out var lo)) return false;
        luid = ((long)hi << 32) | lo;
        end = loEnd;
        return true;
    }
}
