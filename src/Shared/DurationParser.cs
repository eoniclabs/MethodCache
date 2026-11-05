using System;
using System.Globalization;

namespace MethodCache.Internal
{
    // Compiled into the runtime, analyzer and generator so all configuration surfaces agree.
    // A duration is one way: a positive number and a unit, "30s", "10m", "1.5h" or "7d".
    internal static class DurationParser
    {
        /// <summary>What an error message asks for.</summary>
        internal const string Expected = "a positive number and a unit, such as \"30s\", \"10m\", \"1.5h\" or \"7d\"";

        internal static bool TryParse(string? text, bool allowOff, out TimeSpan value)
        {
            value = default;
            if (text == null) return false;
            text = text.Trim();
            if (allowOff && string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Length < 2) return false;
            var suffix = char.ToLowerInvariant(text[text.Length - 1]);
            long scale;
            switch (suffix)
            {
                case 's': scale = TimeSpan.TicksPerSecond; break;
                case 'm': scale = TimeSpan.TicksPerMinute; break;
                case 'h': scale = TimeSpan.TicksPerHour; break;
                case 'd': scale = TimeSpan.TicksPerDay; break;
                default: return false;
            }
            var number = text.Substring(0, text.Length - 1);
            if (!DecimalDigits(number)) return false;
            // A nonzero digit beyond these limits cannot yield whole ticks. Reject before
            // decimal.TryParse can round a higher-precision fraction into an integer value.
            var fractionalLimit = suffix == 's' ? 7 : suffix == 'm' ? 9 : suffix == 'h' ? 11 : 14;
            var point = number.IndexOf('.');
            if (point >= 0)
            {
                var last = number.Length - 1;
                while (last > point && number[last] == '0') last--;
                if (last - point > fractionalLimit) return false;
            }
            if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return false;
            try
            {
                var ticks = amount * scale;
                if (ticks != decimal.Truncate(ticks) || ticks < 1 || ticks > long.MaxValue) return false;
                value = TimeSpan.FromTicks((long)ticks);
                return true;
            }
            catch (OverflowException) { return false; }
        }

        private static bool DecimalDigits(string number)
        {
            var dot = false;
            for (var i = 0; i < number.Length; i++)
            {
                if (number[i] >= '0' && number[i] <= '9') continue;
                if (number[i] != '.' || dot || i == 0 || i == number.Length - 1) return false;
                dot = true;
            }
            return number.Length > 0;
        }

        /// <summary>
        /// The duration written with a unit, for a TimeSpan such as "00:10:00" ("10m"), or null for other text.
        /// </summary>
        internal static string? Suggest(string? text)
        {
            if (text == null || !TryClock(text.Trim(), out var value)) return null;
            return Format(value);
        }

        /// <summary>The largest unit that gives a whole number, or seconds with a fraction: "90m", "7d", "0.2s".</summary>
        internal static string Format(TimeSpan value)
        {
            var ticks = value.Ticks;
            if (ticks % TimeSpan.TicksPerDay == 0) return (ticks / TimeSpan.TicksPerDay).ToString(CultureInfo.InvariantCulture) + "d";
            if (ticks % TimeSpan.TicksPerHour == 0) return (ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture) + "h";
            if (ticks % TimeSpan.TicksPerMinute == 0) return (ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture) + "m";
            return (ticks / (decimal)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture) + "s";
        }

        private static bool TryClock(string text, out TimeSpan value)
        {
            value = default;
            var colon = text.IndexOf(':');
            if (colon < 2) return false;
            var start = colon - 2;
            if (start > 0)
            {
                if (text[start - 1] != '.' || start < 2) return false;
                for (var i = 0; i < start - 1; i++) if (text[i] < '0' || text[i] > '9') return false;
            }
            var remaining = text.Length - start;
            if (remaining < 8 || remaining > 16 || text[start + 2] != ':' || text[start + 5] != ':') return false;
            for (var i = 0; i < remaining; i++)
            {
                if (i == 2 || i == 5) continue;
                if (i == 8) { if (text[start + i] != '.' || remaining == 9) return false; continue; }
                if (text[start + i] < '0' || text[start + i] > '9') return false;
            }
            return TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out value) && value > TimeSpan.Zero;
        }
    }
}
