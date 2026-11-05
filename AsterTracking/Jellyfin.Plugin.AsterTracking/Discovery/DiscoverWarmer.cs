using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AsterTracking.Discovery;

/// <summary>Keeps the Discover pages of people who use Discover built ahead of time, every quarter of an hour.</summary>
public sealed class DiscoverWarmer : IHostedService, IDisposable
{
    private readonly HomeService _home;
    private Timer? _timer;
    private int _running;

    public DiscoverWarmer(HomeService home)
    {
        _home = home;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Warm(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void Warm()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _home.WarmAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }
}
