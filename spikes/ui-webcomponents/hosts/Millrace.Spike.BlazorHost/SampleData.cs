namespace Millrace.Spike.BlazorHost;

/// <summary>Enough jobs, in enough states, for the table and its filters to show something.</summary>
public sealed class SampleData(IJobClient jobs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var i in Enumerable.Range(1, 40))
        {
            await jobs.EnqueueAsync<IDemoWork>(w => w.RunAsync(i), ct: ct);
        }

        foreach (var i in Enumerable.Range(1, 5))
        {
            await jobs.ScheduleAsync<IDemoWork>(w => w.RunAsync(i), TimeSpan.FromHours(i), ct: ct);
        }
    }
}

public interface IDemoWork
{
    Task RunAsync(int value);
}

public sealed class DemoWork : IDemoWork
{
    public Task RunAsync(int value) => value % 7 == 0
        ? throw new InvalidOperationException($"Demo failure for {value}.")
        : Task.CompletedTask;
}
