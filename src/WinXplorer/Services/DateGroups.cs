using System;

namespace WinXplorer.Services
{
    /// <summary>"Group by date" headings like Explorer's: Today, Yesterday, Earlier this week … A long time ago.</summary>
    public static class DateGroups
    {
        private static readonly string[] Keys =
        {
            "DgToday", "DgYesterday", "DgThisWeek", "DgLastWeek", "DgThisMonth", "DgLastMonth", "DgThisYear", "DgLongAgo", "DgUnknown",
        };

        /// <summary>0 = today (or later) … 7 = a long time ago, 8 = no date.</summary>
        public static int Rank(DateTime d)
        {
            if (d == DateTime.MinValue) return 8;
            var today = DateTime.Today;
            if (d >= today) return 0;
            if (d >= today.AddDays(-1)) return 1;
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
            if (d >= weekStart) return 2;
            if (d >= weekStart.AddDays(-7)) return 3;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            if (d >= monthStart) return 4;
            if (d >= monthStart.AddMonths(-1)) return 5;
            if (d >= new DateTime(today.Year, 1, 1)) return 6;
            return 7;
        }

        public static string Name(int rank) => Loc.T(Keys[Math.Clamp(rank, 0, Keys.Length - 1)]);
    }
}
