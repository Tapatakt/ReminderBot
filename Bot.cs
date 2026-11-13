using System.Collections.Concurrent;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ReminderBot;

/// <summary>
/// Бот для управления напоминаниями/заметками по датам
/// </summary>
public class Bot : TelegramBotBase
{
    private readonly string _notesFileName;
    private List<DateNote> _notes;
    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<long, ConcurrentQueue<int>> _sentMessageIds = new(); // chatId -> messageIds
    private const int MaxTrackedMessagesPerChat = 100;
    private const int MaxTotalTrackedMessages = 10000;
    private int _totalTrackedMessages = 0;
    private readonly CancellationTokenSource _schedulerCts = new();

    /// <summary>
    /// Создаёт экземпляр бота и инициализирует команды
    /// </summary>
    public Bot(string token, string notesFileName, long adminId)
        : base(token, [], [adminId])
    {
        _notesFileName = notesFileName;
        _notes = [];

        LoadNotesAndChats();
        RegisterCommands();

        // Запускаем фоновую задачу планировщика
        Task.Run(() => RunSchedulerAsync(_schedulerCts.Token));
    }

    /// <summary>
    /// Регистрирует все команды бота
    /// </summary>
    private void RegisterCommands()
    {
        // Админские команды (только в админском чате)
        RegisterAdminCommand("load", CommandLoad);
        RegisterAdminCommand("upload", CommandUpload);
        RegisterAdminCommand("chats", CommandChats);
        RegisterAdminCommand("addchat", CommandAddChat);
        RegisterAdminCommand("removechat", CommandRemoveChat);
        RegisterAdminCommand("help", CommandAdminHelp);
        // Общедоступные команды
        RegisterUserCommand("ping", CommandPing);
        RegisterUserCommand("add", CommandAdd);
        RegisterUserCommand("get", CommandGet);
        RegisterUserCommand("getall", CommandGetAll);
        RegisterUserCommand("remove", CommandRemove);
        RegisterUserCommand("clean", CommandClean);
        RegisterUserCommand("help", CommandHelp);
    }

    /// <summary>
    /// Обработчик для сообщений без распознанных команд
    /// </summary>
    protected override Task DefaultResponse(long chatId, string username, string message)
        => Task.CompletedTask; // Ничего не делаем для неизвестных команд

    private async Task CommandPing(long chatId, string args)
    {
        Console.WriteLine("ping");
        await SendWithTracking(chatId, $"pong! chatId: {chatId}");
    }

    private async Task CommandAdd(long chatId, string args)
    {
        Console.WriteLine("add");
        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            string fullCommand = $"/add {args}";
            DateNote note = new(fullCommand, chatId, isAddCommand: true);
            _notes.Add(note);
            await SaveNotesAsync();
            await SendWithTracking(chatId, $"✅ Заметка сохранена на {note.Date:dd.MM.yyyy}");
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandGet(long chatId, string args)
    {
        Console.WriteLine("get");
        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            string fullCommand = $"/get {args}";
            List<DateNote> foundNotes = DateNote.FindNotes(_notes, fullCommand, chatId, DateTime.Now.Date);

            if (foundNotes.Count == 0)
            {
                await SendWithTracking(chatId, "🔍 Заметки не найдены");
                return;
            }

            StringBuilder response = new();
            DateTime lastDate = DateTime.MinValue;
            int number = 1;
            foreach (DateNote note in foundNotes)
            {
                if (note.Date != lastDate)
                {
                    response.AppendLine($"📅 {note.Date:dd.MM.yyyy} ({note.DayName()})");
                    lastDate = note.Date;
                    number = 1;
                }
                response.AppendLine($"{number}. {note.Text}");
                number++;
            }
            await SendWithTracking(chatId, response.ToString());
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandGetAll(long chatId, string args)
    {
        Console.WriteLine("getall");
        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            DateTime today = DateTime.Now.Date;

            List<DateNote> foundNotes = [.. _notes
                .Where(n => n.ChatId == chatId && n.Date.Date >= today)
                .OrderBy(n => n.Date.Date)
                .ThenBy(n => n.CreatedAt)];

            if (foundNotes.Count == 0)
            {
                await SendWithTracking(chatId, "🔍 Заметки не найдены");
                return;
            }

            StringBuilder response = new();
            DateTime lastDate = DateTime.MinValue;
            int number = 1;
            foreach (DateNote note in foundNotes)
            {
                if (note.Date != lastDate)
                {
                    response.AppendLine($"📅 {note.Date:dd.MM.yyyy} ({note.DayName()})");
                    lastDate = note.Date;
                    number = 1;
                }
                response.AppendLine($"{number}. {note.Text}");
                number++;
            }
            await SendWithTracking(chatId, response.ToString());
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandRemove(long chatId, string args)
    {
        Console.WriteLine("remove");
        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                await SendWithTracking(chatId, "❌ Укажите дату и номер: /remove <дата> <номер>\nПример: /remove 15.02 2");
                return;
            }

            string dateStr = string.Join(' ', parts[..^1]);
            if (!int.TryParse(parts[^1], out int number) || number < 1)
            {
                await SendWithTracking(chatId, "❌ Номер должен быть положительным числом");
                return;
            }

            DateTime targetDate;
            try
            {
                targetDate = DateNote.ParseSingleDate(dateStr);
            }
            catch (Exception ex)
            {
                await SendWithTracking(chatId, $"❌ Ошибка парсинга даты: {ex.Message}");
                return;
            }

            List<DateNote> notesOnDate = [.. _notes
                .Where(n => n.ChatId == chatId && n.Date.Date == targetDate.Date)
                .OrderBy(n => n.CreatedAt)];

            if (number > notesOnDate.Count)
            {
                await SendWithTracking(chatId, $"❌ На {targetDate:dd.MM.yyyy} найдено только {notesOnDate.Count} заметок");
                return;
            }

            DateNote noteToRemove = notesOnDate[number - 1];
            _notes.Remove(noteToRemove);
            await SaveNotesAsync();
            await SendWithTracking(chatId, $"✅ Заметка \"{noteToRemove.Text}\" удалена");
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandLoad(long chatId, string args)
    {
        Console.WriteLine("load");
        // Эта команда доступна только в админском чате (проверка в ProcessUpdateAsync)
        if (!File.Exists(_notesFileName))
        {
            await SendWithTracking(chatId, "❌ Файл с заметками не найден");
            return;
        }

        await SendFileAsync(chatId, _notesFileName, $"📄 Файл с заметками ({_notes.Count} шт.)");
    }

    private async Task CommandUpload(long chatId, string args)
    {
        Console.WriteLine("upload");
        if (_currentMessage?.Document == null)
        {
            await SendWithTracking(chatId, "❌ Прикрепите файл к команде /upload");
            return;
        }

        string fileId = _currentMessage.Document.FileId;
        var fileInfo = await _botClient.GetFile(fileId, cancellationToken: _currentCancellationToken);

        if (fileInfo.FilePath == null)
        {
            await SendWithTracking(chatId, "❌ Не удалось получить информацию о файле");
            return;
        }

        string tempFilePath = Path.GetTempFileName();
        bool tempFileMoved = false;
        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            // Скачиваем во временный файл под семафором
            await DownloadFileAsync(fileId, tempFilePath);

            // Валидируем файл
            ValidationResult validation = await ValidateNotesFileAsync(tempFilePath);
            if (!validation.IsValid)
            {
                await SendWithTracking(chatId, $"❌ Файл не прошёл валидацию: {validation.ErrorMessage}");
                return;
            }

            // Создаем резервную копию текущего файла
            if (File.Exists(_notesFileName))
            {
                string backupFileName = $"{_notesFileName}.backup";
                if (File.Exists(backupFileName))
                    File.Delete(backupFileName);
                File.Copy(_notesFileName, backupFileName);
            }

            // Перемещаем временный файл на место
            File.Move(tempFilePath, _notesFileName, overwrite: true);
            tempFileMoved = true;

            // Перезагружаем заметки из нового файла
            _notes.Clear();
            _allowedChatIds.Clear();
            LoadNotesAndChats();
            await SendWithTracking(chatId, $"✅ Файл загружен!\n📝 Загружено заметок: {_notes.Count}\n📋 Разрешённых чатов: {_allowedChatIds.Count}");
        }
        finally
        {
            _fileSemaphore.Release();
            if (!tempFileMoved && File.Exists(tempFilePath))
                File.Delete(tempFilePath);
        }
    }

    /// <summary>
    /// Валидирует файл заметок перед загрузкой
    /// </summary>
    private async Task<ValidationResult> ValidateNotesFileAsync(string filePath)
    {
        try
        {
            string[] lines = await File.ReadAllLinesAsync(filePath);
            if (lines.Length == 0)
                return ValidationResult.Fail("Файл пуст");

            // Проверяем заголовок
            if (!lines[0].StartsWith("#allowed_chats:"))
                return ValidationResult.Fail("Первая строка должна содержать #allowed_chats:");

            // Проверяем каждую заметку
            int lineNumber = 0;
            foreach (string line in lines)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#allowed_chats:"))
                    continue;

                try
                {
                    _ = new DateNote(line);
                }
                catch (Exception ex)
                {
                    return ValidationResult.Fail($"Ошибка в строке {lineNumber}: {ex.Message}");
                }
            }

            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail($"Ошибка чтения файла: {ex.Message}");
        }
    }

    /// <summary>
    /// Результат валидации файла
    /// </summary>
    private readonly record struct ValidationResult(bool IsValid, string ErrorMessage)
    {
        public static ValidationResult Success() => new(true, string.Empty);
        public static ValidationResult Fail(string message) => new(false, message);
    }
    private async Task CommandAdminHelp(long chatId, string args)
    {
        Console.WriteLine("help");
        StringBuilder s = new();
        s.Append("Список команд:\n");
        s.Append("/ping - пинг. Также выдаёт id чата.\n");
        s.Append("/add <дата>: <заметка> - записать заметку\n");
        s.Append("/get <дата> - получить все заметки на эту дату (с номерами)\n");
        s.Append("/get <дата>-<дата> - получить заметки за диапазон\n");
        s.Append("/get <дата>- - получить заметки от даты и далее\n");
        s.Append("/get -<дата> - получить заметки начиная с сегодня и до даты\n");
        s.Append("/getall - получить все заметки начиная с сегодня\n");
        s.Append("/remove <дата> <номер> - удалить заметку\n");
        s.Append("/clean - удалить сообщения бота (текущая сессия)\n");
        s.Append("/help - получить список команд\n");
        s.Append("\nТолько админ:\n");
        s.Append("/load - запросить файл с заметками\n");
        s.Append("/upload - заменить файл с заметками \n");
        s.Append("/chats - список разрешённых чатов \n");
        s.Append("/addchat <id> - добавить чат \n");
        s.Append("/removechat <id> - удалить чат \n");
        s.Append("\nПримеры допустимых дат:\n");
        s.Append("Вторник\nСб\n26\n18.08\n16 января\n23 июл\n18.09.2026\n20.02.26\n2026.12.28\n");
        s.Append("Только последние два формата позволяют обращаться к прошедшим датам");
        await SendWithTracking(chatId, s.ToString());
    }

    private async Task CommandHelp(long chatId, string args)
    {
        Console.WriteLine("help");
        StringBuilder s = new();
        s.Append("Список команд:\n");
        s.Append("/ping - пинг. Также выдаёт id чата.\n");
        s.Append("/add <дата>: <заметка> - записать заметку\n");
        s.Append("/get <дата> - получить все заметки на эту дату (с номерами)\n");
        s.Append("/get <дата>-<дата> - получить заметки за диапазон\n");
        s.Append("/get <дата>- - получить заметки от даты и далее\n");
        s.Append("/get -<дата> - получить заметки начиная с сегодня и до даты\n");
        s.Append("/getall - получить все заметки начиная с сегодня\n");
        s.Append("/remove <дата> <номер> - удалить заметку\n");
        s.Append("/clean - удалить сообщения бота (текущая сессия)\n");
        s.Append("/help - получить список команд\n");
        s.Append("\nПримеры допустимых дат:\n");
        s.Append("Вторник\nСб\n26\n18.08\n16 января\n23 июл\n18.09.2026\n20.02.26\n2026.12.28\n");
        s.Append("Только последние два формата позволяют обращаться к прошедшим датам");
        await SendWithTracking(chatId, s.ToString());
    }

    private async Task CommandChats(long chatId, string args)
    {
        Console.WriteLine("chats");
        StringBuilder sb = new();
        sb.AppendLine("📋 Разрешённые чаты:");
        foreach (long id in _allowedChatIds)
            sb.AppendLine($"{id}");
        await SendWithTracking(chatId, sb.ToString());
    }

    private async Task CommandAddChat(long chatId, string args)
    {
        Console.WriteLine("addchat");
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !long.TryParse(parts[0], out long newChatId))
        {
            await SendWithTracking(chatId, "❌ Укажите id чата: /addchat <id>");
            return;
        }

        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            if (_allowedChatIds.Add(newChatId))
            {
                await SaveNotesAsync();
                await SendWithTracking(chatId, $"✅ Чат {newChatId} добавлен");
            }
            else
            {
                await SendWithTracking(chatId, $"⚠️ Чат {newChatId} уже в списке");
            }
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandRemoveChat(long chatId, string args)
    {
        Console.WriteLine("removechat");
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !long.TryParse(parts[0], out long chatIdToRemove))
        {
            await SendWithTracking(chatId, "❌ Укажите id чата: /removechat <id>");
            return;
        }

        await _fileSemaphore.WaitAsync(_currentCancellationToken);
        try
        {
            if (_allowedChatIds.Remove(chatIdToRemove))
            {
                await SaveNotesAsync();
                await SendWithTracking(chatId, $"✅ Чат {chatIdToRemove} удалён");
            }
            else
            {
                await SendWithTracking(chatId, $"⚠️ Чат {chatIdToRemove} не найден в списке");
            }
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    private async Task CommandClean(long chatId, string args)
    {
        Console.WriteLine("clean");

        if (!_sentMessageIds.TryGetValue(chatId, out ConcurrentQueue<int>? messageQueue) || messageQueue.IsEmpty)
        {
            await SendWithTracking(chatId, "ℹ️ Нет сообщений для удаления (бот не отслеживает сообщения старше текущей сессии)");
            return;
        }

        int deletedCount = 0;
        int failedCount = 0;
        List<int> messageIds = [.. messageQueue];
        while (messageQueue.TryDequeue(out _)) { }
        Interlocked.Add(ref _totalTrackedMessages, -messageIds.Count);

        foreach (int msgId in messageIds)
        {
            try
            {
                await DeleteMessageAsync(chatId, msgId);
                deletedCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось удалить сообщение {msgId}: {ex.Message}");
                failedCount++;
            }
        }

        // Отправляем результат напрямую без отслеживания
        await SendTextWithReplyAsync(chatId, $"🧹 Удалено {deletedCount} сообщений" + (failedCount > 0 ? $", не удалось: {failedCount}" : ""));
    }

    /// <summary>
    /// Отправляет сообщение и отслеживает его ID для команды /clean
    /// </summary>
    private async Task SendWithTracking(long chatId, string text)
    {
        Message sent = await SendTextWithReplyAsync(chatId, text);
        TrackMessage(chatId, sent.MessageId);
    }

    private void TrackMessage(long chatId, int messageId)
    {
        ConcurrentQueue<int> queue = _sentMessageIds.GetOrAdd(chatId, _ => new ConcurrentQueue<int>());
        queue.Enqueue(messageId);
        int total = Interlocked.Increment(ref _totalTrackedMessages);
        // Ограничиваем историю последними 100 сообщениями на чат
        if (queue.Count > MaxTrackedMessagesPerChat)
            queue.TryDequeue(out _);
        // Общее ограничение на все сообщения
        if (total > MaxTotalTrackedMessages)
        {
            // Удаляем старые сообщения из всех чатов
            foreach (ConcurrentQueue<int> q in _sentMessageIds.Values)
                while (q.Count > MaxTrackedMessagesPerChat / 2 && q.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _totalTrackedMessages, _sentMessageIds.Values.Sum(q => q.Count));
        }
    }

    private void LoadNotesAndChats()
    {
        if (!File.Exists(_notesFileName))
        {
            File.WriteAllText(_notesFileName, "#allowed_chats: ", Encoding.UTF8);
            return;
        }

        using StreamReader sr = new(_notesFileName, Encoding.UTF8);

        string? firstLine = sr.ReadLine();
        if (firstLine != null && firstLine.StartsWith("#allowed_chats:"))
        {
            string chatsPart = firstLine["#allowed_chats:".Length..].Trim();
            foreach (long chatId in chatsPart.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s.Trim())))
                _allowedChatIds.Add(chatId);
        }

        int lineNumber = 1;
        while (!sr.EndOfStream)
        {
            lineNumber++;
            string? line = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                _notes.Add(new(line));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка загрузки строки {lineNumber}: {ex.Message}");
            }
        }
    }

    private async Task SaveNotesAsync()
    {
        List<string> lines = [];
        lines.Add($"#allowed_chats: {string.Join(", ", _allowedChatIds)}");
        lines.AddRange(_notes.Select(n => n.ToString()));
        await File.WriteAllLinesAsync(_notesFileName, lines, Encoding.UTF8);
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DateTime now = DateTime.Now;
                DateTime nextRun = now.Date.AddHours(1); // сегодня 01:00

                // Если сегодня 01:00 уже прошло, ждём до завтра 01:00
                if (now.Hour >= 1)
                    nextRun = nextRun.AddDays(1);

                TimeSpan delay = nextRun - now;

                // Ждем до 01:00
                await Task.Delay(delay, cancellationToken);

                // Наступило 01:00!
                await SendDailyDigestAsync(cancellationToken);

                // Ждем небольшую паузу (1 мин), чтобы случайно не сработать дважды
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в планировщике: {ex.Message}");
                // Если произошла ошибка, ждем 6 часов и пробуем снова
                await Task.Delay(TimeSpan.FromHours(6), cancellationToken);
            }
        }
    }

    private async Task SendDailyDigestAsync(CancellationToken cancellationToken)
    {
        // 1. Определяем логику: воскресенье (ночь с сб на вс) или обычный день
        DateTime today = DateTime.Now.Date; // В момент запуска это уже будет 00:00 "сегодня"

        // Если сейчас Воскресенье 00:00, значит это ночь с Субботы на Воскресенье.
        // Если сегодня Воскресенье, показываем на 8 дней (Вс...Вс). Иначе на 2 дня (Сегодня + Завтра).
        int daysLookahead = (today.DayOfWeek == DayOfWeek.Sunday) ? 8 : 2;

        DateTime endDate = today.AddDays(daysLookahead);

        await _fileSemaphore.WaitAsync(cancellationToken);
        Dictionary<long, List<DateNote>> notesByChat;
        try
        {
            // Группируем заметки по чатам
            notesByChat = _notes
                .Where(n => n.Date.Date >= today && n.Date.Date < endDate)
                .GroupBy(n => n.ChatId)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Date).ToList());
        }
        finally
        {
            _fileSemaphore.Release();
        }

        if (notesByChat.Count == 0) return;

        string periodName = (daysLookahead == 8) ? "неделю" : "2 дня";

        // 2. Рассылаем по чатам
        foreach (var (chatId, notes) in notesByChat)
        {
            if (notes.Count == 0) continue;

            // Формируем сообщение для конкретного чата
            StringBuilder sb = new();
            sb.AppendLine($"<b>🔔 Напоминание на {periodName}:</b>");
            sb.AppendLine();

            DateTime lastDate = DateTime.MinValue;
            foreach (DateNote note in notes)
            {
                if (note.Date != lastDate)
                {
                    sb.AppendLine($"📅 {note.Date:dd.MM.yyyy} ({note.DayName()})");
                    lastDate = note.Date;
                }
                sb.AppendLine($"{note.Text}");
                sb.AppendLine();
            }

            try
            {
                await _botClient.SendMessage(
                    chatId: chatId,
                    text: sb.ToString(),
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Не удалось отправить дайджест в чат {chatId}: {ex.Message}");
            }
        }
    }
}
