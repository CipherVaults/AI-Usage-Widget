using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AIUsageWidget
{
    internal sealed class QuotaUpdates : IDisposable
    {
        private static readonly TimeSpan NormalRefreshInterval = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ResetGraceDelay = TimeSpan.FromSeconds(5);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<Task<QuotaReading>> fetch;
        private readonly Action<QuotaReading> show;
        private TimeSpan nextCheckAt;
        private int retryDelaySeconds;
        private bool requesting;
        private bool disposed;

        public QuotaUpdates(Func<Task<QuotaReading>> fetch, Action<QuotaReading> show)
        {
            this.fetch = fetch;
            this.show = show;
        }

        public void Poll()
        {
            if (!disposed && !requesting && clock.Elapsed >= nextCheckAt)
                Refresh();
        }

        public async void Refresh()
        {
            if (disposed || requesting)
                return;

            requesting = true;
            try
            {
                var reading = await fetch();
                if (disposed)
                    return;

                if (reading != null && reading.Problem == null)
                {
                    retryDelaySeconds = 0;
                    nextCheckAt = clock.Elapsed + NextSuccessInterval(reading);
                }
                else
                {
                    retryDelaySeconds = Math.Min(60, Math.Max(5, retryDelaySeconds * 2));
                    nextCheckAt = clock.Elapsed + TimeSpan.FromSeconds(retryDelaySeconds);
                }
                if (reading != null)
                    show(reading);
            }
            catch (Exception)
            {
                if (!disposed)
                {
                    retryDelaySeconds = Math.Min(60, Math.Max(5, retryDelaySeconds * 2));
                    nextCheckAt = clock.Elapsed + TimeSpan.FromSeconds(retryDelaySeconds);
                }
            }
            finally
            {
                requesting = false;
            }
        }

        internal static TimeSpan NextSuccessInterval(QuotaReading reading)
        {
            var interval = NormalRefreshInterval;
            if (reading == null || reading.Pools == null)
                return interval;

            var now = DateTimeOffset.UtcNow;
            foreach (var pool in reading.Pools)
            {
                if (pool == null || pool.Windows == null)
                    continue;
                foreach (var window in pool.Windows)
                {
                    if (window == null || !window.Reset.HasValue)
                        continue;
                    var untilReset = window.Reset.Value - now;
                    if (untilReset > TimeSpan.Zero)
                    {
                        var candidate = untilReset + ResetGraceDelay;
                        if (candidate < interval)
                            interval = candidate;
                    }
                }
            }
            return interval < MinimumRefreshInterval ? MinimumRefreshInterval : interval;
        }

        public void Dispose()
        {
            disposed = true;
        }
    }
}
