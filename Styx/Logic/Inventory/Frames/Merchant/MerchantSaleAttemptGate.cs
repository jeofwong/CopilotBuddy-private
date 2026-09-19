using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Styx.Logic.Inventory.Frames.Merchant
{
    // A local submission is not a server acknowledgement. Suppress the same
    // observed player/merchant/slot/link/count for a bounded interval, even if
    // another item was sold. No item is deleted or permanently blacklisted.
    internal sealed class MerchantSaleAttemptGate
    {
        private const int MaximumKeys = 256;
        private const long RetryMilliseconds = 120000;
        private const long PendingPollMilliseconds = 1000;
        private const long PendingLimitMilliseconds = 10000;
        private readonly Dictionary<string, long> attempts = new(StringComparer.Ordinal);
        private int executing;
        private string prefix;
        private long pendingSince = -1;
        private long nextPendingQueryAt = -1;

        internal int Execute(string script, Func<string, List<string>> query, long now)
            => ExecuteCore(script, query, now, batch: false);

        internal int ExecuteBatch(string script, Func<string, List<string>> query, long now)
            => ExecuteCore(script, query, now, batch: true);

        private int ExecuteCore(string script, Func<string, List<string>> query, long now, bool batch)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (now < 0 || now > long.MaxValue - RetryMilliseconds) return -1;
            if (Interlocked.CompareExchange(ref executing, 1, 0) != 0) return 2;
            try
            {
                if (!batch && pendingSince >= 0)
                {
                    if (now < pendingSince)
                        ClearPendingObservation();
                    else if (now - pendingSince >= PendingLimitMilliseconds)
                    {
                        ClearPendingObservation();
                        return 4;
                    }
                    else if (now < nextPendingQueryAt)
                        return 2;
                }

                foreach (string key in attempts.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                {
                    attempts.Remove(key);
                    prefix = null;
                }
                // Backpressure is not an empty bag or a confirmed sale.
                if (attempts.Count >= MaximumKeys) return 4;
                if (prefix == null)
                {
                    var text = new StringBuilder("local blockedSaleStacks={");
                    foreach (string key in attempts.Keys.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        text.Append("[\"");
                        foreach (byte value in Encoding.UTF8.GetBytes(key))
                            text.Append('\\').Append(value.ToString("D3", CultureInfo.InvariantCulture));
                        text.Append("\"]=true,");
                    }
                    prefix = text.Append("};").ToString();
                }
                // Bound generated source as well as the number of retained keys.
                if (prefix.Length > 65536) return 4;
                int available = MaximumKeys - attempts.Count;
                string budget = batch
                    ? "local saleAttemptBudget=" + available.ToString(CultureInfo.InvariantCulture) + ";"
                    : string.Empty;
                List<string> values = query(prefix + budget + script);
                if (values == null || values.Count < 2 || values[0] != "ok" ||
                    !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int result) ||
                    result < 0 || result > (batch ? 4 : 3))
                {
                    if (!batch) MarkPendingObservation(now);
                    return -1;
                }
                if (batch)
                {
                    // A batch returns its terminal state plus every attempted key,
                    // including attempts before a pending/closed/error boundary.
                    // Validate all keys first: malformed data cannot partly publish.
                    if (result == 1 || values.Count - 2 > available) return -1;
                    var receipts = new HashSet<string>(StringComparer.Ordinal);
                    for (int index = 2; index < values.Count; index++)
                    {
                        string key = values[index];
                        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) > 2048
                            || attempts.ContainsKey(key) || !receipts.Add(key)) return -1;
                    }
                    foreach (string key in receipts) attempts[key] = now + RetryMilliseconds;
                    if (receipts.Count != 0) prefix = null;
                    return result;
                }
                if (result != 1)
                {
                    if (values.Count != 2)
                    {
                        MarkPendingObservation(now);
                        return -1;
                    }
                    if (result == 2)
                        MarkPendingObservation(now);
                    else
                        ClearPendingObservation();
                    return result;
                }
                if (values.Count != 3 || string.IsNullOrEmpty(values[2]) ||
                    Encoding.UTF8.GetByteCount(values[2]) > 2048)
                {
                    MarkPendingObservation(now);
                    return -1;
                }

                attempts[values[2]] = now + RetryMilliseconds;
                prefix = null;
                ClearPendingObservation();
                return 1;
            }
            finally { Volatile.Write(ref executing, 0); }
        }

        private void MarkPendingObservation(long now)
        {
            if (pendingSince < 0 || now < pendingSince)
                pendingSince = now;
            nextPendingQueryAt = now + PendingPollMilliseconds;
        }

        private void ClearPendingObservation()
        {
            pendingSince = -1;
            nextPendingQueryAt = -1;
        }
    }
}
