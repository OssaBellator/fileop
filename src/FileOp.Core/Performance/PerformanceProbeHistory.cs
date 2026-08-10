namespace FileOp.Core.Performance;

public sealed record PerformanceProbeDistribution(
    PerformanceProbeKind Kind,
    string Name,
    string Scope,
    int SampleCount,
    int SampleCapacity,
    long MinimumMicroseconds,
    long MedianMicroseconds,
    long? P95Microseconds,
    long MaximumMicroseconds,
    DateTimeOffset FirstCapturedAt,
    DateTimeOffset LastCapturedAt);

public sealed class PerformanceProbeHistory
{
    public const int DefaultSampleCapacity = 20;
    public const int MinimumSamplesForP95 = 5;

    private readonly int _sampleCapacity;
    private readonly Dictionary<ProbeKey, Queue<ProbeSample>> _samples = [];

    public PerformanceProbeHistory(int sampleCapacity = DefaultSampleCapacity)
    {
        if (sampleCapacity <= 0 || sampleCapacity > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCapacity),
                sampleCapacity,
                "Performance probe history capacity must be between 1 and 1000 samples per probe.");
        }

        _sampleCapacity = sampleCapacity;
    }

    public IReadOnlyList<PerformanceProbeDistribution> AddAndSummarize(
        PerformanceDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var probe in snapshot.Probes)
        {
            if (probe.Kind is not (PerformanceProbeKind.Search or PerformanceProbeKind.Storage))
            {
                continue;
            }

            var key = new ProbeKey(probe.Kind, probe.Name, probe.Scope);
            if (!_samples.TryGetValue(key, out var samples))
            {
                samples = new Queue<ProbeSample>(_sampleCapacity);
                _samples.Add(key, samples);
            }

            samples.Enqueue(new ProbeSample(
                snapshot.CapturedAt.ToUniversalTime(),
                Math.Max(0, probe.ElapsedMicroseconds)));
            while (samples.Count > _sampleCapacity)
            {
                samples.Dequeue();
            }
        }

        return SummarizeCurrentSnapshot(snapshot);
    }

    public IReadOnlyList<PerformanceProbeDistribution> SummarizeCurrentSnapshot(
        PerformanceDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new List<PerformanceProbeDistribution>(2);
        var seen = new HashSet<ProbeKey>();

        foreach (var probe in snapshot.Probes)
        {
            if (probe.Kind is not (PerformanceProbeKind.Search or PerformanceProbeKind.Storage))
            {
                continue;
            }

            var key = new ProbeKey(probe.Kind, probe.Name, probe.Scope);
            if (!seen.Add(key) || !_samples.TryGetValue(key, out var samples) || samples.Count == 0)
            {
                continue;
            }

            result.Add(CreateDistribution(key, samples));
        }

        return result;
    }

    private PerformanceProbeDistribution CreateDistribution(
        ProbeKey key,
        Queue<ProbeSample> samples)
    {
        var ordered = samples
            .Select(static sample => sample.ElapsedMicroseconds)
            .Order()
            .ToArray();
        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? Midpoint(ordered[middle - 1], ordered[middle])
            : ordered[middle];
        long? p95 = null;
        if (ordered.Length >= MinimumSamplesForP95)
        {
            var rank = Math.Clamp(
                checked((int)Math.Ceiling(ordered.Length * 0.95d)) - 1,
                0,
                ordered.Length - 1);
            p95 = ordered[rank];
        }

        return new PerformanceProbeDistribution(
            key.Kind,
            key.Name,
            key.Scope,
            ordered.Length,
            _sampleCapacity,
            ordered[0],
            median,
            p95,
            ordered[^1],
            samples.Peek().CapturedAt,
            samples.Last().CapturedAt);
    }

    private static long Midpoint(long left, long right) =>
        left + ((right - left) / 2);

    private sealed record ProbeKey(
        PerformanceProbeKind Kind,
        string Name,
        string Scope);

    private sealed record ProbeSample(
        DateTimeOffset CapturedAt,
        long ElapsedMicroseconds);
}
