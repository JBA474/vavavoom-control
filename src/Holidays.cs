// The holidays the Gallery and the playlist know: for each, the Divoom searches that find its designs, the words that
// mark a design as belonging to it (in the languages Divoom's artists use most), and the days it is in season.
using System;
using System.Text.RegularExpressions;

namespace AuraBoxControl
{
    class Holiday
    {
        public string Name, Searches;          // Searches: Divoom searches shown together, "|" between them
        public Regex Words;                    // a design whose name matches belongs to this holiday
        public Func<DateTime, bool> InSeason;  // null: never in season (Birthdays, More Holidays)
    }

    static class Holidays
    {
        static Regex W(string pattern) { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }

        static bool Between(DateTime d, int m1, int d1, int m2, int d2)
        {
            int k = d.Month * 100 + d.Day, a = m1 * 100 + d1, b = m2 * 100 + d2;
            return a <= b ? k >= a && k <= b : k >= a || k <= b;   // a range may wrap past New Year's Day
        }

        // US Thanksgiving: the fourth Thursday of November
        public static DateTime Thanksgiving(int year)
        {
            var d = new DateTime(year, 11, 1);
            while (d.DayOfWeek != DayOfWeek.Thursday) d = d.AddDays(1);
            return d.AddDays(21);
        }

        // Easter Sunday (Gregorian calendar, the anonymous "Meeus/Jones/Butcher" method)
        public static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateTime(y, month, day);
        }

        public static readonly Holiday[] All = {
            new Holiday { Name = "Halloween", Searches = "halloween|pumpkin|ghost|haunted",
                Words = W(@"hallowe+en|pumpkin|haunted|jack.?o.?lantern|ghost|ghoul|spook|calabaza|citrouille|kürbis|zucca|abóbora|fantasma|fantôme|gespenst|призрак|привидени|тыкв|хэллоуин|ハロウィン|かぼちゃ|カボチャ|おばけ|お化け|ゴースト|南瓜|万圣|萬聖|幽灵|幽靈|鬼魂|할로윈|호박|유령"),
                InSeason = d => d.Month == 10 },
            new Holiday { Name = "Thanksgiving", Searches = "thanksgiving",
                Words = W(@"thanksgiving|感恩节|感恩節|추수감사절|action de grâce"),
                InSeason = d => d.Month == 11 && d.Date <= Thanksgiving(d.Year) },
            new Holiday { Name = "Christmas", Searches = "christmas|xmas|santa|snowman",
                Words = W(@"christmas|x-?mas|santa|snowman|reindeer|navidad|noël|\bnoel\b|weihnacht|natale|\bnatal\b|рождеств|クリスマス|サンタ|圣诞|聖誕|크리스마스"),
                InSeason = d => d.Month == 11 && d.Date > Thanksgiving(d.Year) || Between(d, 12, 1, 12, 26) },
            new Holiday { Name = "New Year", Searches = "new year|lunar new year",
                Words = W(@"new\s*year|neujahr|nouvel\s*an|a[ñn]o\s*nuevo|ano\s*novo|capodanno|silvester|новы[йм]\s*год|新年|元旦|春节|春節|새해"),
                InSeason = d => Between(d, 12, 27, 1, 7) },
            new Holiday { Name = "Valentine's Day", Searches = "valentine",
                Words = W(@"valentin|バレンタイン|情人节|情人節|발렌타인"),
                InSeason = d => Between(d, 2, 1, 2, 14) },
            new Holiday { Name = "St. Patrick's Day", Searches = "st patrick|shamrock",
                Words = W(@"st\.?\s*patrick|saint\s*patrick|shamrock|leprechaun"),
                InSeason = d => Between(d, 3, 1, 3, 17) },
            new Holiday { Name = "Easter", Searches = "easter",
                Words = W(@"easter|ostern|osterhase|pascua|pâques|paques|pasqua|páscoa|пасх|イースター|复活节|復活節|부활절"),
                InSeason = d => d.Date >= Easter(d.Year).AddDays(-14) && d.Date <= Easter(d.Year) },
            new Holiday { Name = "Fourth of July", Searches = "4th of july|july 4th|fourth of july",
                Words = W(@"4th\s*of\s*july|july\s*4|fourth\s*of\s*july|independence\s*day"),
                InSeason = d => Between(d, 6, 27, 7, 4) },
            new Holiday { Name = "Birthdays", Searches = "birthday",
                Words = W(@"birthday|cumpleaños|anniversaire|geburtstag|compleanno|aniversário|день\s*рождени|誕生日|生日|생일"),
                InSeason = null },
            new Holiday { Name = "More Holidays", Searches = "hanukkah|diwali|ramadan|mother's day|father's day",
                Words = W(@"hanukk?ah|chanukk?ah|diwali|ramadan|\beid\b|mother'?s\s*day|father'?s\s*day"),
                InSeason = null } };

        // the holiday a design's name says it belongs to, or null
        public static Holiday Of(string name)
        {
            foreach (var h in All) if (h.Words.IsMatch(name ?? "")) return h;
            return null;
        }

        // the holiday in season on this day, or null
        public static Holiday Current(DateTime day)
        {
            foreach (var h in All) if (h.InSeason != null && h.InSeason(day)) return h;
            return null;
        }

        // the next holiday to come into season after this day (for "nothing in season" messages), and when
        public static Holiday Next(DateTime day, out DateTime when)
        {
            for (int k = 1; k <= 366; k++)
            {
                var d = day.Date.AddDays(k);
                var h = Current(d);
                if (h != null && h != Current(day)) { when = d; return h; }
            }
            when = day; return null;
        }

        public static Holiday Named(string name)
        {
            foreach (var h in All) if (h.Name == name) return h;
            return null;
        }
    }
}
