using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AIUsageWidget
{
    internal sealed class QuotaUpdates : IDisposable
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<Task<QuotaReading>> fetch;
        private readonly Action<QuotaReading> show;
        private TimeSpan retryAt;
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
            if (clock.Elapsed >= retryAt)
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
                    retryAt = TimeSpan.Zero;
                }
                else
                {
                    DelayRetry();
                }
                if (reading != null)
                    show(reading);
            }
            catch (Exception)
            {
                if (!disposed)
                    DelayRetry();
            }
            finally
            {
                requesting = false;
            }
        }

        private void DelayRetry()
        {
            retryDelaySeconds = Math.Min(60, Math.Max(5, retryDelaySeconds * 2));
            retryAt = clock.Elapsed + TimeSpan.FromSeconds(retryDelaySeconds);
        }

        public void Dispose()
        {
            disposed = true;
        }
    }
}
