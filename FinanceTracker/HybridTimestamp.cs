using System;
using System.Globalization;

namespace FinanceTracker.Models
{
    /// Гибридные логические часы: (PhysicalMs, Logical).
    /// Сравнение: сначала по PhysicalMs, при равенстве — по Logical.
    public readonly struct HybridTimestamp : IComparable<HybridTimestamp>, IEquatable<HybridTimestamp>
    {
        public long PhysicalMs { get; }
        public int Logical { get; }

        public HybridTimestamp(long physicalMs, int logical)
        {
            PhysicalMs = physicalMs;
            Logical = logical;
        }

        public static HybridTimestamp Now() =>
            new HybridTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);

        public int CompareTo(HybridTimestamp other)
        {
            int c = PhysicalMs.CompareTo(other.PhysicalMs);
            return c != 0 ? c : Logical.CompareTo(other.Logical);
        }

        public bool Equals(HybridTimestamp other) =>
            PhysicalMs == other.PhysicalMs && Logical == other.Logical;

        public override bool Equals(object? obj) => obj is HybridTimestamp t && Equals(t);

        public override int GetHashCode() => HashCode.Combine(PhysicalMs, Logical);

        public static bool operator >(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) > 0;
        public static bool operator <(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) < 0;
        public static bool operator >=(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) >= 0;
        public static bool operator <=(HybridTimestamp a, HybridTimestamp b) => a.CompareTo(b) <= 0;
        public static bool operator ==(HybridTimestamp a, HybridTimestamp b) => a.Equals(b);
        public static bool operator !=(HybridTimestamp a, HybridTimestamp b) => !a.Equals(b);

        /// Формат: "physicalMs:logical" (например, "1700000000000:3").
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0}:{1}", PhysicalMs, Logical);

        /// Парсит новый формат "ms:logical".
        /// Для обратной совместимости умеет читать и старый ISO-8601 UTC ("0001-01-01T00:00:00.0000000Z").
        public static bool TryParse(string? s, out HybridTimestamp result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            int idx = s.IndexOf(':');
            if (idx > 0 && idx < s.Length - 1)
            {
                var left = s.Substring(0, idx);
                var right = s.Substring(idx + 1);

                // Отсекаем время вида "12:34" и ISO — там long.TryParse/ int.TryParse упадут
                if (long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) &&
                    int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lg))
                {
                    result = new HybridTimestamp(ms, lg);
                    return true;
                }
            }

            // Fallback: старый формат (ISO 8601)
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            {
                long unixMs = new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();
                result = new HybridTimestamp(unixMs, 0);
                return true;
            }

            return false;
        }
    }
}