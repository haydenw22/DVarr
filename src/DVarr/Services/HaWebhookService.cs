using System.Text;

namespace DVarr.Services;

/// <summary>
/// Forwards live recording status deltas to a Home Assistant webhook (set <c>ha_webhook_url</c>).
/// Webhook + REST is the primary HA channel (no MQTT broker on the host); MQTT is an optional later add.
/// </summary>
public sealed class HaWebhookService : BackgroundService
{
    private readonly RecordingEventBus _bus;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HaWebhookService> _log;

    public HaWebhookService(RecordingEventBus bus, IServiceScopeFactory scopes, IHttpClientFactory httpFactory, ILogger<HaWebhookService> log)
    { _bus = bus; _scopes = scopes; _httpFactory = httpFactory; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (id, reader) = _bus.Subscribe();
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            await foreach (var msg in reader.ReadAllAsync(stoppingToken))
            {
                string? url;
                using (var scope = _scopes.CreateScope())
                    url = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync("ha_webhook_url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                try { await http.PostAsync(url, new StringContent(msg, Encoding.UTF8, "application/json"), stoppingToken); }
                catch (Exception ex) { _log.LogDebug(ex, "[HA] webhook post failed"); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _bus.Unsubscribe(id); }
    }
}
