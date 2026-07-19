using System.Globalization;
using System.Text.RegularExpressions;

namespace ReminderBot;

public partial class DateNote
{
    public DateTime Date { get; private set; }
    public string Text { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public long ChatId { get; private set; }
    // Конструктор для создания новой заметки из команды /add
    public DateNote(string message, long chatId, bool isAddCommand = false)
    {
        message = message.Trim();

        Match match = RegexAdd().Match(message);
        if (!match.Success)
            throw new ArgumentException("Неверный формат сообщения. Ожидается: /add <дата>: <текст>");

        string dateStr = match.Groups[1].Value.Trim();
        Text = match.Groups[2].Value.Trim();
        Date = ParseDate(dateStr);
        CreatedAt = DateTime.Now;
        ChatId = chatId;
    }

    // Конструктор для загрузки из файла (chat_id извлекается из строки)
    public DateNote(string lineFromFile)
    {
        string message = lineFromFile.Trim();

        Match toStringMatch = RegexFromFile().Match(message);
        if (!toStringMatch.Success)
            throw new ArgumentException("Неверный формат строки файла");

        Date = DateTime.ParseExact(toStringMatch.Groups[1].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture);
        Text = toStringMatch.Groups[2].Value.Trim().Replace("\\n", "\n");
        CreatedAt = DateTime.ParseExact(toStringMatch.Groups[3].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture);

        string chatIdStr = toStringMatch.Groups[4].Value.Trim();
        if (string.IsNullOrEmpty(chatIdStr))
            throw new ArgumentException("Неверный формат строки файла: отсутствует chat_id");
        ChatId = long.Parse(chatIdStr);
    }

    public static List<DateNote> FindNotes(List<DateNote> notes, string queryMessage, long chatId, DateTime? fromDate = null)
    {
        // Ожидаем формат: /get <дата или диапазон>
        Match match = RegexGet().Match(queryMessage.Trim());

        if (!match.Success)
            return [];

        string query = match.Groups[1].Value.Trim();

        // Проверяем на открытый диапазон от даты: /get <дата>-
        Match openEndMatch = RegexOpenEndRange().Match(query);
        if (openEndMatch.Success)
        {
            string startStr = openEndMatch.Groups[1].Value.Trim();
            DateTime startDate = ParseDate(startStr);

            return [.. notes
                    .Where(n => n.ChatId == chatId && n.Date.Date >= startDate.Date)
                    .OrderBy(n => n.Date.Date)
                    .ThenBy(n => n.CreatedAt)];
        }

        // Проверяем на открытый диапазон до даты: /get -<дата>
        Match openStartMatch = RegexOpenStartRange().Match(query);
        if (openStartMatch.Success)
        {
            string endStr = openStartMatch.Groups[1].Value.Trim();
            DateTime endDate = ParseDate(endStr);
            DateTime startDate = fromDate ?? DateTime.Now.Date;

            return [.. notes
                    .Where(n => n.ChatId == chatId && n.Date.Date >= startDate.Date && n.Date.Date <= endDate.Date)
                    .OrderBy(n => n.Date.Date)
                    .ThenBy(n => n.CreatedAt)];
        }

        // Проверяем, закрытый диапазон ли это: /get <дата>-<дата>
        Match rangeMatch = RegexRange().Match(query);
        if (rangeMatch.Success)
        {
            string startStr = rangeMatch.Groups[1].Value.Trim();
            string endStr = rangeMatch.Groups[2].Value.Trim();

            DateTime startDate = ParseDate(startStr);
            DateTime endDate = ParseDate(endStr, referenceDate: startDate);

            return [.. notes
                    .Where(n => n.ChatId == chatId && n.Date.Date >= startDate.Date && n.Date.Date <= endDate.Date)
                    .OrderBy(n => n.Date.Date)
                    .ThenBy(n => n.CreatedAt)];
        }
        else
        {
            // Одна дата
            DateTime targetDate = ParseDate(query);

            return [.. notes
                    .Where(n => n.ChatId == chatId && n.Date.Date == targetDate.Date)
                    .OrderBy(n => n.CreatedAt)];
        }
    }

    private static DateTime ParseDate(string dateStr, DateTime? referenceDate = null)
    {
        DateTime now = referenceDate ?? DateTime.Now;
        dateStr = dateStr.Trim();

        // dd.MM.yyyy
        if (RegexDdmmyyyy().IsMatch(dateStr))
        {
            return DateTime.ParseExact(dateStr, "d.M.yyyy", CultureInfo.InvariantCulture);
        }

        // dd.MM.yy
        if (RegexDdmmyy().IsMatch(dateStr))
        {
            var parts = dateStr.Split('.');
            int day = int.Parse(parts[0]);
            int month = int.Parse(parts[1]);
            int year = 2000 + int.Parse(parts[2]);
            // Валидация дня и месяца
            if (month < 1 || month > 12)
                throw new ArgumentException($"Неверный месяц: {month}");
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
                throw new ArgumentException($"Неверный день для месяца {month}: {day}");
            return new DateTime(year, month, day);
        }

        // yyyy.MM.dd
        if (RegexYyyymmdd().IsMatch(dateStr))
        {
            return DateTime.ParseExact(dateStr, "yyyy.M.d", CultureInfo.InvariantCulture);
        }

        // dd.MM
        if (RegexDdmm().IsMatch(dateStr))
        {
            var parts = dateStr.Split('.');
            int day = int.Parse(parts[0]);
            int month = int.Parse(parts[1]);

            DateTime date = new(now.Year, month, day);

            if (date < now.Date)
                date = date.AddYears(1);

            return date;
        }

        // dd (день месяца)
        if (RegexDd().IsMatch(dateStr))
        {
            int year = now.Year;
            int month = now.Month;
            int day = int.Parse(dateStr);
            if (day < now.Day)
            {
                month++;
                if (month == 13)
                {
                    month = 1;
                    year++;
                }
            }
            // Проверяем, что день влезает в месяц (на случай если сейчас февраль, а day=30)
            while (day > DateTime.DaysInMonth(year, month))
            {
                month++;
                if (month == 13)
                {
                    month = 1;
                    year++;
                }
            }
            return new DateTime(year, month, day);
        }

        // День недели (полный или сокращенный)
        DayOfWeek? dayOfWeek = ParseDayOfWeek(dateStr);
        if (dayOfWeek.HasValue)
        {
            DateTime date = now.Date;

            while (date.DayOfWeek != dayOfWeek.Value)
            {
                date = date.AddDays(1);
            }

            if (date < now.Date)
                date = date.AddDays(7);

            return date;
        }

        // dd <месяц>
        Match monthMatch = RegexDdMonth().Match(dateStr);
        if (monthMatch.Success)
        {
            int day = int.Parse(monthMatch.Groups[1].Value);
            string monthStr = monthMatch.Groups[2].Value.Trim().ToLower();

            int? month = ParseMonth(monthStr);
            if (month.HasValue)
            {
                DateTime date = new(now.Year, month.Value, day);

                if (date < now.Date)
                    date = date.AddYears(1);

                return date;
            }
        }

        throw new ArgumentException($"Не удалось распознать дату: {dateStr}");
    }

    // Публичный метод для парсинга одиночной даты
    public static DateTime ParseSingleDate(string dateStr)
    {
        return ParseDate(dateStr);
    }

    private static DayOfWeek? ParseDayOfWeek(string str)
    {
        str = str.ToLower().Trim();

        Dictionary<string, DayOfWeek> days = new()
            {
                { "понедельник", DayOfWeek.Monday }, { "пн", DayOfWeek.Monday },
                { "вторник", DayOfWeek.Tuesday }, { "вт", DayOfWeek.Tuesday },
                { "среда", DayOfWeek.Wednesday }, { "ср", DayOfWeek.Wednesday },
                { "четверг", DayOfWeek.Thursday }, { "чт", DayOfWeek.Thursday },
                { "пятница", DayOfWeek.Friday }, { "пт", DayOfWeek.Friday },
                { "суббота", DayOfWeek.Saturday }, { "сб", DayOfWeek.Saturday },
                { "воскресенье", DayOfWeek.Sunday }, { "вс", DayOfWeek.Sunday }
            };

        foreach (KeyValuePair<string, DayOfWeek> kvp in days)
        {
            if (str == kvp.Key || (LevenshteinDistance(str, kvp.Key) < 2 && kvp.Key.Length >= 3))
                return kvp.Value;
        }

        return null;
    }

    private static int? ParseMonth(string str)
    {
        str = str.ToLower().Trim();

        Dictionary<string, int> months = new()
            {
                { "январь", 1 }, { "января", 1 }, { "янв", 1 },
                { "февраль", 2 }, { "февраля", 2 }, { "фев", 2 },
                { "март", 3 }, { "марта", 3 }, { "мар", 3 },
                { "апрель", 4 }, { "апреля", 4 }, { "апр", 4 },
                { "май", 5 }, { "мая", 5 },
                { "июнь", 6 }, { "июня", 6 }, { "июн", 6 },
                { "июль", 7 }, { "июля", 7 }, { "июл", 7 },
                { "август", 8 }, { "августа", 8 }, { "авг", 8 },
                { "сентябрь", 9 }, { "сентября", 9 }, { "сен", 9 },
                { "октябрь", 10 }, { "октября", 10 }, { "окт", 10 },
                { "ноябрь", 11 }, { "ноября", 11 }, { "ноя", 11 },
                { "декабрь", 12 }, { "декабря", 12 }, { "дек", 12 }
            };

        foreach (KeyValuePair<string, int> kvp in months)
            if (str == kvp.Key) return kvp.Value;

        return null;
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        int[,] d = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            d[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            d[0, j] = j;

        for (int j = 1; j <= s2.Length; j++)
            for (int i = 1; i <= s1.Length; i++)
            {
                int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }

        return d[s1.Length, s2.Length];
    }

    public override string ToString()
    {
        string safeText = Text.Replace("\n", "\\n").Replace("\r", ""); // экранируем переносы строк
        return $"{Date:dd.MM.yyyy}: {safeText} (от {CreatedAt:dd.MM.yyyy}) [#{ChatId}]";
    }
    public string DayName()
        => Date.ToString("dddd", CultureInfo.GetCultureInfo("ru-RU"));
    [GeneratedRegex(@"^/get\s+(.+)$", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex RegexGet();
    [GeneratedRegex(@"^/add\s+(.+?):\s*(.+)$", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex RegexAdd();
    [GeneratedRegex(@"^(.+?)\s*-\s*(.+)$")]
    private static partial Regex RegexRange();
    [GeneratedRegex(@"^(.+?)\s*-\s*$")]
    private static partial Regex RegexOpenEndRange();
    [GeneratedRegex(@"^-\s*(.+)$")]
    private static partial Regex RegexOpenStartRange();
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{4}$")]
    private static partial Regex RegexDdmmyyyy();
    [GeneratedRegex(@"^\d{4}\.\d{1,2}\.\d{1,2}$")]
    private static partial Regex RegexYyyymmdd();
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}$")]
    private static partial Regex RegexDdmm();
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{2}$")]
    private static partial Regex RegexDdmmyy();
    [GeneratedRegex(@"^\d{1,2}$")]
    private static partial Regex RegexDd();
    [GeneratedRegex(@"^(\d{1,2})\s+(.+)$")]
    private static partial Regex RegexDdMonth();
    [GeneratedRegex(@"^(\d{2}\.\d{2}\.\d{4}):\s*(.+?)\s*\(от\s+(\d{2}\.\d{2}\.\d{4})\)\s*\[#(-?\d+)\]$")]
    private static partial Regex RegexFromFile();
}