using FlowMind.Infrastructure.DocumentProcessing;

namespace FlowMind.Worker;

/// <summary>Polls the job table and processes documents one at a time. Drains the queue, then sleeps.</summary>
public sealed class JobPollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JobPollingWorker> _log;
    private readonly TimeSpan _poll;

    public JobPollingWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<JobPollingWorker> log)
    {
        _scopes = scopes;
        _log = log;
        _poll = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Worker:PollSeconds", 5)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("FlowMind worker started (poll every {Poll}s)", _poll.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool didWork;
                do
                {
                    using var scope = _scopes.CreateScope();
                    didWork = await scope.ServiceProvider.GetRequiredService<DocumentJobRunner>().RunNextAsync(stoppingToken);
                } while (didWork && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error; will retry");
            }

            try { await Task.Delay(_poll, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
