using System.Collections.Concurrent;
using Millrace.Workflows;

namespace Millrace.Sample.Api;

/// <summary>Records what ran, so the sample can show its work at <c>GET /log</c>.</summary>
public sealed class SampleLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IEnumerable<string> Entries => _entries;

    public void Add(string message)
    {
        _entries.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {message}");
        while (_entries.Count > 200 && _entries.TryDequeue(out _))
        {
        }
    }
}

// The interfaces jobs are enqueued against. Millrace captures the *declared* type, so these are what
// it records — the implementations can change freely without touching jobs already in the queue.

public interface IEmailSender
{
    Task SendConfirmationAsync(string orderId);

    Task SendReminderAsync(string orderId);

    Task SendInvoiceAsync(string orderId);
}

public interface IReportService
{
    Task GenerateAsync(string name);
}

public interface INotifier
{
    Task NotifySettledAsync(string orderId);
}

public sealed class EmailSender(SampleLog log) : IEmailSender
{
    public Task SendConfirmationAsync(string orderId)
    {
        log.Add($"confirmation email for {orderId}");
        return Task.CompletedTask;
    }

    public Task SendReminderAsync(string orderId)
    {
        log.Add($"reminder email for {orderId}");
        return Task.CompletedTask;
    }

    public Task SendInvoiceAsync(string orderId)
    {
        log.Add($"invoice email for {orderId}");
        return Task.CompletedTask;
    }
}

public sealed class ReportService(SampleLog log) : IReportService
{
    public Task GenerateAsync(string name)
    {
        log.Add($"generated report '{name}'");
        return Task.CompletedTask;
    }
}

public sealed class Notifier(SampleLog log) : INotifier
{
    public Task NotifySettledAsync(string orderId)
    {
        log.Add($"notified: {orderId} settled");
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------- workflow

public sealed class OnboardingData
{
    public string CustomerId { get; set; } = "cust-1";

    public bool NeedsApproval { get; set; }

    public bool Approved { get; set; }
}

public sealed record ApprovalDecision(bool IsApproved);

public sealed class CreateAccount(SampleLog log) : IActivity<OnboardingData>
{
    public Task ExecuteAsync(ActivityContext<OnboardingData> context, CancellationToken ct)
    {
        log.Add($"created account for {context.Data.CustomerId}");
        return Task.CompletedTask;
    }
}

public sealed class SendWelcome(SampleLog log) : IActivity<OnboardingData>
{
    public Task ExecuteAsync(ActivityContext<OnboardingData> context, CancellationToken ct)
    {
        log.Add($"welcomed {context.Data.CustomerId} (approved: {context.Data.Approved})");
        return Task.CompletedTask;
    }
}

/// <summary>
/// One workflow showing the pieces that matter: a branch, a durable wait for an external decision,
/// and a step that runs either way.
/// </summary>
/// <remarks>
/// While waiting for the signal the instance holds no job at all — it is a row, and costs nothing.
/// Deliver the decision with <c>POST /millrace/api/v1/signals/approval/{customerId}</c>.
/// </remarks>
public sealed class OnboardingWorkflow : IWorkflow<OnboardingData>
{
    public string Id => "onboarding";

    public int Version => 1;

    public void Build(IWorkflowBuilder<OnboardingData> flow) => flow
        .StartWith<CreateAccount>()
        .If(
            d => d.NeedsApproval,
            approval => approval.WaitForSignal<ApprovalDecision>(
                "approval",
                d => d.CustomerId,
                (d, decision) => d.Approved = decision.IsApproved,
                timeout: TimeSpan.FromDays(3)))
        .Then<SendWelcome>();
}
