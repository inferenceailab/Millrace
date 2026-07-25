using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Millrace.Diagnostics;
using Millrace.Storage.InMemory;
using Xunit;

namespace Millrace.Tests.Diagnostics;

/// <summary>
/// Traces and metrics (#42, §8).
/// </summary>
/// <remarks>
/// <para>
/// The claim worth testing is propagation: a request that fires work into the background should
/// still show that work in its own trace, even though the two are separated by a queue, a process
/// and usually a machine.
/// </para>
/// <para>
/// Every assertion here is scoped to this test's own job or queue. Traces and metrics are process
/// global, and other test classes run jobs in parallel — an unscoped assertion would be reading
/// their telemetry, which is how the first cut of these tests managed to observe a "failed" outcome
/// for a job that succeeded.
/// </para>
/// </remarks>
public sealed class ObservabilityTests
{
    public interface IWork
    {
        Task RunAsync(string value);

        Task FailAsync();
    }

    private sealed class Work : IWork
    {
        public Task RunAsync(string value) => Task.CompletedTask;

        public Task FailAsync() => throw new InvalidOperationException("boom");
    }

    private static IHost BuildHost(string queue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<IWork, Work>();
        builder.Services.AddMillrace(m => m
            .UseInMemoryStorage()
            .Configure(o =>
            {
                o.Queues.Clear();
                o.Queues.Add(queue);
                o.MinPollDelay = TimeSpan.FromMilliseconds(5);
                o.MaxPollDelay = TimeSpan.FromMilliseconds(20);
                o.SchedulerInterval = TimeSpan.FromMilliseconds(5);
                o.DefaultRetry = Retry.None;
            }));

        return builder.Build();
    }

    private static ActivityListener Listen(string sourceName, Action<Activity>? onStopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = onStopped,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Waits for the span belonging to one specific job.</summary>
    private static async Task<Activity?> WaitForSpanAsync(List<Activity> spans, JobId id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (spans)
            {
                var match = spans.FirstOrDefault(s => (string?)s.GetTagItem("millrace.job.id") == id.ToString());
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(15);
        }

        return null;
    }

    private static (List<Activity> Spans, ActivityListener Listener) CollectSpans()
    {
        var spans = new List<Activity>();
        var listener = Listen(MillraceDiagnostics.SourceName, activity =>
        {
            lock (spans)
            {
                spans.Add(activity);
            }
        });

        return (spans, listener);
    }

    [Fact]
    public async Task A_job_continues_the_trace_that_enqueued_it()
    {
        var (spans, listener) = CollectSpans();
        using var _ = listener;
        // A source with no listener never creates an activity, so the caller's listener has to be
        // registered before the span it is meant to produce.
        using var callerListener = Listen("Test");

        using var host = BuildHost("otel-trace");
        await host.StartAsync();

        using var caller = new ActivitySource("Test").StartActivity("POST /orders");
        Assert.NotNull(caller);

        var id = await host.Services.GetRequiredService<IJobClient>()
            .EnqueueAsync<IWork>(w => w.RunAsync("x"), new EnqueueOptions { Queue = "otel-trace" });

        var span = await WaitForSpanAsync(spans, id);

        Assert.NotNull(span);
        // The whole point: the worker's span joins the caller's trace rather than starting a new one.
        Assert.Equal(caller.TraceId, span.TraceId);
        Assert.Equal(ActivityKind.Consumer, span.Kind);
        Assert.Equal("otel-trace", span.GetTagItem("millrace.job.queue"));
    }

    [Fact]
    public async Task A_job_enqueued_outside_a_trace_still_gets_its_own_span()
    {
        var (spans, listener) = CollectSpans();
        using var _ = listener;
        using var host = BuildHost("otel-orphan");
        await host.StartAsync();

        var id = await host.Services.GetRequiredService<IJobClient>()
            .EnqueueAsync<IWork>(w => w.RunAsync("x"), new EnqueueOptions { Queue = "otel-orphan" });

        var span = await WaitForSpanAsync(spans, id);

        Assert.NotNull(span);
        Assert.Contains("RunAsync", span.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_job_marks_its_span_as_an_error()
    {
        var (spans, listener) = CollectSpans();
        using var _ = listener;
        using var host = BuildHost("otel-fail");
        await host.StartAsync();

        var id = await host.Services.GetRequiredService<IJobClient>()
            .EnqueueAsync<IWork>(w => w.FailAsync(), new EnqueueOptions { Queue = "otel-fail" });

        var span = await WaitForSpanAsync(spans, id);

        Assert.NotNull(span);
        // Otherwise debugging means correlating the trace with the job record by hand.
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains("boom", span.StatusDescription);
    }

    [Fact]
    public async Task Duration_and_completion_are_measured_with_the_outcome()
    {
        const string Queue = "otel-metrics";
        var measurements = new List<(string Instrument, string? Queue, string? Outcome)>();

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MillraceDiagnostics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? queue = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "millrace.queue")
                {
                    queue = tag.Value?.ToString();
                }
                else if (tag.Key == "millrace.outcome")
                {
                    outcome = tag.Value?.ToString();
                }
            }

            lock (measurements)
            {
                measurements.Add((instrument.Name, queue, outcome));
            }
        }

        meterListener.SetMeasurementEventCallback<double>((i, m, t, s) => Record(i, t));
        meterListener.SetMeasurementEventCallback<long>((i, m, t, s) => Record(i, t));
        meterListener.Start();

        using var host = BuildHost(Queue);
        await host.StartAsync();
        await host.Services.GetRequiredService<IJobClient>()
            .EnqueueAsync<IWork>(w => w.RunAsync("x"), new EnqueueOptions { Queue = Queue });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (measurements)
            {
                if (measurements.Any(m => m.Instrument == "millrace.jobs.completed" && m.Queue == Queue))
                {
                    break;
                }
            }

            await Task.Delay(15);
        }

        lock (measurements)
        {
            var mine = measurements.Where(m => m.Queue == Queue).ToList();
            var seen = string.Join(", ", mine.Select(m => $"{m.Instrument}[{m.Outcome ?? "-"}]"));

            Assert.True(
                mine.Any(m => m.Instrument == "millrace.job.queue_latency"),
                $"no queue latency recorded; saw: {seen}");
            Assert.True(
                mine.Any(m => m.Instrument == "millrace.job.duration" && m.Outcome == "succeeded"),
                $"no succeeded duration recorded; saw: {seen}");
            Assert.True(
                mine.Any(m => m.Instrument == "millrace.jobs.completed" && m.Outcome == "succeeded"),
                $"no succeeded completion recorded; saw: {seen}");
        }
    }

    [Fact]
    public async Task The_trace_parent_is_persisted_on_the_job()
    {
        using var callerListener = Listen("Test");

        var services = new ServiceCollection();
        services.AddMillrace(m => m.UseInMemoryStorage().Configure(o => o.WorkerEnabled = false));
        services.AddScoped<IWork, Work>();
        using var provider = services.BuildServiceProvider();

        using var caller = new ActivitySource("Test").StartActivity("caller");
        var id = await provider.GetRequiredService<IJobClient>().EnqueueAsync<IWork>(w => w.RunAsync("x"));

        var stored = await provider.GetRequiredService<InMemoryStorage>()
            .GetJobAsync(id, CancellationToken.None);

        // Persisted because the execution happens elsewhere, with no ambient context to inherit.
        Assert.Equal(caller!.Id, stored!.TraceParent);
    }
}
