using System.Net;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// Runs the exporters of <c>Ops:Metrics</c> (docs/ops/metrics.md): the Prometheus scrape listener and/or the OTLP push
/// loop, both reading <see cref="MetricsStore.Collect"/> off the world thread. Disabled: nothing starts. An exporter
/// that cannot start (a port in use) logs an error and the daemon keeps running; metrics are never a reason to stop.
/// </summary>
public sealed class MetricsHost(MetricsOptions options, MetricsStore store, ILogger<MetricsHost> logger, string serviceName) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private HttpListener? _listener;
    private Task? _prometheus;
    private Task? _otlp;
    private HttpClient? _http;

    /// <summary>The bound Prometheus prefix, or null when that exporter is not running.</summary>
    public string? PrometheusPrefix => _listener?.Prefixes.FirstOrDefault();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        store.Start();
        _ = RuntimeMeters.Meter;
        if (options.Exporter is MetricsExporter.Prometheus or MetricsExporter.Both)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(options.PrometheusPrefix);
                listener.Start();
                _listener = listener;
                _prometheus = Task.Run(ServePrometheusAsync, CancellationToken.None);
                logger.LogInformation("metrics: Prometheus endpoint on {Prefix}", options.PrometheusPrefix);
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
            {
                logger.LogError(ex, "metrics: the Prometheus endpoint {Prefix} could not start; metrics are not served", options.PrometheusPrefix);
            }
        }

        if (options.Exporter is MetricsExporter.Otlp or MetricsExporter.Both)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _otlp = Task.Run(PushOtlpAsync, CancellationToken.None);
            logger.LogInformation("metrics: OTLP push to {Endpoint} every {Interval} s", options.OtlpEndpoint, options.OtlpIntervalSeconds);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (Task? task in new[] { _prometheus, _otlp })
        {
            if (task is not null)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public void Dispose()
    {
        _stop.Dispose();
        _listener?.Close();
        _http?.Dispose();
    }

    private async Task ServePrometheusAsync()
    {
        HttpListener listener = _listener!;
        while (!_stop.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException ex)
            {
                logger.LogWarning(ex, "metrics: Prometheus listener error");
                continue;
            }

            try
            {
                byte[] body = Encoding.UTF8.GetBytes(PrometheusFormatter.Render(store.Collect(), options.Realm));
                context.Response.ContentType = PrometheusFormatter.ContentType;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The scraper went away; the next scrape is a new request.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private async Task PushOtlpAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.OtlpIntervalSeconds));
        bool failing = false;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                byte[] body = OtlpJsonFormatter.Render(store.Collect(), serviceName, options.Realm, store.StartTime, DateTimeOffset.UtcNow);
                try
                {
                    using var content = new ByteArrayContent(body);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    using HttpResponseMessage response = await _http!.PostAsync(new Uri(options.OtlpEndpoint), content, _stop.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode && !failing)
                    {
                        failing = true;
                        logger.LogWarning("metrics: OTLP push to {Endpoint} answered {Status}", options.OtlpEndpoint, (int)response.StatusCode);
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        failing = false;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !_stop.IsCancellationRequested)
                {
                    if (!failing)
                    {
                        failing = true;
                        logger.LogWarning("metrics: OTLP push to {Endpoint} failed: {Message}", options.OtlpEndpoint, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
