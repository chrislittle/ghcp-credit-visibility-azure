namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Thread-safe holder for the most recent GitHub rate-limit headers seen by the billing client.
    /// Registered as a singleton so the (transient) HTTP client can record into it and the diagnostics
    /// collector can read it, without either depending on the other.
    ///
    /// Null readings mean "no GitHub response has been observed this process lifetime yet" — e.g. mock
    /// mode, or before the first snapshot run. That is deliberately distinct from a real remaining
    /// count of 0 (rate-limit exhausted), which is exactly the condition we want an alert on.
    /// </summary>
    public sealed class GitHubRateLimitState
    {
        private long _remaining = -1;   // -1 sentinel = never observed
        private long _limit = -1;
        private long _lastSeenTicks;    // 0 = never

        /// <summary>Record the rate-limit headers from a completed GitHub response.</summary>
        public void Record(int? remaining, int? limit)
        {
            if (remaining is int r) Interlocked.Exchange(ref _remaining, r);
            if (limit is int l) Interlocked.Exchange(ref _limit, l);
            Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);
        }

        public int? Remaining
        {
            get { var v = Interlocked.Read(ref _remaining); return v < 0 ? null : (int)v; }
        }

        public int? Limit
        {
            get { var v = Interlocked.Read(ref _limit); return v < 0 ? null : (int)v; }
        }

        public DateTime? LastSeenUtc
        {
            get { var t = Interlocked.Read(ref _lastSeenTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
        }
    }
}
