using System;
using System.Globalization;

namespace SkyShare.Planner.Core {

    /// <summary>Parses "HH:MM:SS", "+DD:MM:SS" and "HH:MM" strings.</summary>
    public static class Sexagesimal {

        public static bool TryParseRaHours(string s, out double hours) {
            hours = 0;
            if (!TrySplit(s, out var neg, out var a, out var b, out var c) || neg) return false;
            if (a >= 24 || b >= 60 || c >= 60) return false;
            hours = a + b / 60.0 + c / 3600.0;
            return true;
        }

        public static bool TryParseDecDegrees(string s, out double degrees) {
            degrees = 0;
            if (!TrySplit(s, out var neg, out var a, out var b, out var c)) return false;
            if (b >= 60 || c >= 60) return false;
            var v = a + b / 60.0 + c / 3600.0;
            if (v > 90) return false;
            degrees = neg ? -v : v;
            return true;
        }

        public static bool TryParseHhMm(string s, out TimeOnly time) =>
            TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

        private static bool TrySplit(string s, out bool negative, out double a, out double b, out double c) {
            negative = false; a = b = c = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s[0] is '+' or '-') { negative = s[0] == '-'; s = s[1..]; }
            var parts = s.Split(':');
            if (parts.Length != 3) return false;
            var inv = CultureInfo.InvariantCulture;
            return double.TryParse(parts[0], NumberStyles.Float, inv, out a) && a >= 0
                && double.TryParse(parts[1], NumberStyles.Float, inv, out b) && b >= 0
                && double.TryParse(parts[2], NumberStyles.Float, inv, out c) && c >= 0;
        }
    }
}
