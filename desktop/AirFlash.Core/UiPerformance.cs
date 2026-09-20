using System.Collections.Concurrent;
using System.Diagnostics;
namespace AirFlash.Core;

// Enabled only by the isolated runner; never writes on the UI thread.
public static class UiPerformance
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<double>> Samples = new();
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    public static bool Enabled { get; set; }
    public static double[] ReadSamples(string name) => Samples.TryGetValue(name, out var values) ? values.ToArray() : [];
    public static int ReadCount(string name) => Counts.GetValueOrDefault(name);
    public static void Count(string name) { if (Enabled) Counts.AddOrUpdate(name, 1, (_, count) => count + 1); }
    public static void Record(string name, double milliseconds) { if (Enabled) Samples.GetOrAdd(name, _ => new()).Enqueue(milliseconds); }
    public static IDisposable Measure(string name) => new Measurement(name, Enabled);
    public static object Report() => new
    {
        counts = Counts.ToDictionary(),
        timings = Samples.ToDictionary(p => p.Key, p =>
        {
            var values = p.Value.Order().ToArray();
            return new { count = values.Length, p95_ms = values[(int)Math.Ceiling(values.Length * .95) - 1], max_ms = values[^1], samples_ms = values };
        })
    };
    private sealed class Measurement(string name, bool enabled) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() { if (enabled) Record(name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds); }
    }
}
