using Microsoft.AspNetCore.Mvc;
using Millrace;
using Millrace.Sample.Api;
using Millrace.Workflows;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- Millrace

// One connection string is the only difference between the in-memory story and the durable one.
// Set MILLRACE_POSTGRES (or run `docker compose up -d` and use the default) to see jobs survive a
// restart; leave it unset and everything runs in memory with no setup at all.
var postgres = builder.Configuration["MILLRACE_POSTGRES"];

builder.Services.AddMillrace(millrace =>
{
    if (string.IsNullOrWhiteSpace(postgres))
    {
        millrace.UseInMemoryStorage();
    }
    else
    {
        millrace.UsePostgreSqlStorage(postgres);
    }

    millrace.AddWorkflow<OnboardingWorkflow>();
});

// The dashboard is middleware in this same host — no extra process, no extra deployment.
builder.Services.AddMillraceDashboard();
builder.Services.AddMillraceReactUi();

// Development allows anonymous dashboard access; anywhere else this would be a startup error until
// a hook is registered (ARCHITECTURE.md §11.13).
builder.Services.AddMillraceDashboardAuthorization((context, _) =>
    ValueTask.FromResult(builder.Environment.IsDevelopment()
        || context.Request.Headers["X-Millrace-Key"] == builder.Configuration["MILLRACE_DASHBOARD_KEY"]));

// The services the jobs call. Ordinary DI — Millrace resolves them per execution.
builder.Services.AddSingleton<SampleLog>();
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<INotifier, Notifier>();

var app = builder.Build();

app.MapMillraceDashboard("/millrace");

// ---------------------------------------------------------------- the four job shapes

app.MapPost("/orders/{id}/confirm", async (string id, IJobClient jobs) =>
{
    // Fire-and-forget. The expression is captured, its arguments serialized, and the target
    // resolved from DI when a worker runs it — possibly on another machine.
    var jobId = await jobs.EnqueueAsync<IEmailSender>(s => s.SendConfirmationAsync(id));
    return Results.Ok(new { enqueued = jobId });
});

app.MapPost("/orders/{id}/remind", async (string id, IJobClient jobs, [FromQuery] int seconds = 30) =>
{
    // Delayed. Durable: the delay survives a restart because it is a scheduled row, not a timer.
    var jobId = await jobs.ScheduleAsync<IEmailSender>(
        s => s.SendReminderAsync(id), TimeSpan.FromSeconds(seconds));
    return Results.Ok(new { scheduled = jobId, inSeconds = seconds });
});

app.MapPost("/orders/{id}/settle", async (string id, IJobClient jobs) =>
{
    // Continuation: the notification runs only if the charge succeeds, and is cancelled with it if
    // it does not.
    var charge = await jobs.EnqueueAsync<IEmailSender>(s => s.SendInvoiceAsync(id));
    var notify = await jobs.ContinueWithAsync<INotifier>(charge, n => n.NotifySettledAsync(id));
    return Results.Ok(new { charge, continuation = notify });
});

app.MapPost("/reports/nightly", async (IJobClient jobs) =>
{
    // Recurring. Upserting by id is idempotent, so calling this twice does not create two schedules.
    await jobs.UpsertRecurringAsync<IReportService>(
        "nightly-report", "0 3 * * *", s => s.GenerateAsync("nightly"));
    return Results.Ok(new { recurring = "nightly-report", cron = "0 3 * * * (UTC)" });
});

// ---------------------------------------------------------------- a workflow

app.MapPost("/onboarding", async (IWorkflowClient workflows, [FromBody] OnboardingData data) =>
{
    var instance = await workflows.StartAsync("onboarding", data);
    return Results.Ok(new { instance, signalWith = $"POST /millrace/api/v1/signals/approval/{data.CustomerId}" });
});

// ---------------------------------------------------------------- what happened

app.MapGet("/log", (SampleLog log) => Results.Ok(log.Entries));

app.MapGet("/", () => Results.Redirect("/millrace/ui"));

app.Run();
