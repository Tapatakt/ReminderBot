using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;

namespace ReminderBot;

/// <summary>
/// Базовый абстрактный класс для Telegram-бота
/// </summary>
/// <remarks>
/// Инициализирует базовый класс бота
/// </remarks>
/// <param name="botToken">Токен бота</param>
/// <param name="allowedChatIds">Разрешенные ID чатов</param>
/// <param name="adminIds">ID администраторов</param>
public abstract class TelegramBotBase(
    string botToken, 
    IEnumerable<long> allowedChatIds, 
    IEnumerable<long> adminIds)
{
    protected readonly ITelegramBotClient _botClient = new TelegramBotClient(botToken);
    protected readonly HashSet<long> _allowedChatIds = [.. allowedChatIds];
    protected readonly HashSet<long> _adminIds = [.. adminIds];
    protected readonly Dictionary<string, Func<long, string, Task>> _userCommands = new(StringComparer.OrdinalIgnoreCase);
    protected readonly Dictionary<string, Func<long, string, Task>> _adminCommands = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Текущее обрабатываемое сообщение (доступно внутри обработчиков команд)
    /// </summary>
    protected Message? _currentMessage;

    /// <summary>
    /// CancellationToken текущей операции
    /// </summary>
    protected CancellationToken _currentCancellationToken;

    /// <summary>
    /// Регистрирует команду для всех пользователей
    /// </summary>
    /// <param name="command">Имя команды (без /)</param>
    /// <param name="handler">Обработчик команды</param>
    protected void RegisterUserCommand(string command, Func<long, string, Task> handler)
    {
        _userCommands[command] = handler;
    }

    /// <summary>
    /// Регистрирует команду только для администраторов
    /// </summary>
    /// <param name="command">Имя команды (без /)</param>
    /// <param name="handler">Обработчик команды</param>
    protected void RegisterAdminCommand(string command, Func<long, string, Task> handler)
    {
        _adminCommands[command] = handler;
    }

    /// <summary>
    /// Проверяет, является ли пользователь администратором
    /// </summary>
    /// <param name="userId">ID пользователя</param>
    protected bool IsAdmin(long userId) => _adminIds.Contains(userId);

    /// <summary>
    /// Проверяет, разрешен ли чат для обычных пользователей
    /// </summary>
    protected bool IsChatAllowed(long chatId) => _allowedChatIds.Contains(chatId);

    /// <summary>
    /// Добавляет чат в список разрешённых
    /// </summary>
    protected void AddAllowedChat(long chatId) => _allowedChatIds.Add(chatId);

    /// <summary>
    /// Удаляет чат из списка разрешённых
    /// </summary>
    protected bool RemoveAllowedChat(long chatId) => _allowedChatIds.Remove(chatId);

    /// <summary>
    /// Запускает long polling
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunPollingAsync(_cts.Token));
    }

    /// <summary>
    /// Останавливает polling
    /// </summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Отправляет текстовое сообщение
    /// </summary>
    public async Task<Message> SendTextAsync(long chatId, string text, ParseMode parseMode = ParseMode.None) =>
        await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            cancellationToken: _currentCancellationToken);

    /// <summary>
    /// Отправляет текстовое сообщение с reply на текущее сообщение
    /// </summary>
    public async Task<Message> SendTextWithReplyAsync(long chatId, string text, ParseMode parseMode = ParseMode.None)
    {
        ReplyParameters? replyParameters = _currentMessage != null 
            ? new ReplyParameters { MessageId = _currentMessage.MessageId } 
            : null;

        return await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            replyParameters: replyParameters,
            parseMode: parseMode,
            cancellationToken: _currentCancellationToken);
    }

    /// <summary>
    /// Отправляет файл
    /// </summary>
    public async Task SendFileAsync(long chatId, string filePath, string caption = "")
    {
        ReplyParameters? replyParameters = _currentMessage != null 
            ? new ReplyParameters { MessageId = _currentMessage.MessageId } 
            : null;

        await using Stream stream = System.IO.File.OpenRead(filePath);
        await _botClient.SendDocument(
            chatId: chatId,
            document: InputFile.FromStream(stream, Path.GetFileName(filePath)),
            caption: caption,
            replyParameters: replyParameters,
            cancellationToken: _currentCancellationToken);
    }

    /// <summary>
    /// Скачивает файл
    /// </summary>
    public async Task DownloadFileAsync(string fileId, string destinationPath)
    {
        await using Stream fileStream = System.IO.File.OpenWrite(destinationPath);
        await _botClient.GetInfoAndDownloadFile(fileId, fileStream, _currentCancellationToken);
    }

    /// <summary>
    /// Удаляет сообщение
    /// </summary>
    public async Task DeleteMessageAsync(long chatId, int messageId) =>
        await _botClient.DeleteMessage(chatId, messageId, _currentCancellationToken);

    /// <summary>
    /// Абстрактный метод для обработки сообщений без команд
    /// </summary>
    protected abstract Task DefaultResponse(long chatId, string username, string message);

    /// <summary>
    /// Основной цикл polling
    /// </summary>
    private async Task RunPollingAsync(CancellationToken cancellationToken)
    {
        int offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Update[] updates = [..await _botClient.GetUpdates(offset, cancellationToken: cancellationToken)];

                foreach (Update update in updates)
                {
                    offset = update.Id + 1;
                    await ProcessUpdateAsync(update, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Обрабатывает входящий апдейт
    /// </summary>
    protected virtual async Task ProcessUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
            return;

        string? text = message.Text ?? message.Caption;
        if (text is null)
            return;

        string username = message.From?.Username 
            ?? message.From?.FirstName 
            ?? "Unknown";

        long userId = message.From?.Id ?? 0;
        long chatId = message.Chat.Id;
        bool isAdmin = IsAdmin(userId);
        bool isAllowedChat = IsChatAllowed(chatId);

        // Устанавливаем контекст текущего сообщения
        _currentMessage = message;
        _currentCancellationToken = cancellationToken;

        // Админ может использовать команды в любом чате
        // Обычные пользователи - только в разрешенных чатах
        if (!isAllowedChat && !isAdmin)
            return;

        // Сначала проверяем админские команды (только для админов, в любом чате)
        bool commandHandled = false;
        if (isAdmin && _adminCommands.Count > 0)
            commandHandled = await TryExecuteCommandAsync(text, _adminCommands, chatId);

        // Если не обработано - проверяем обычные команды (только в разрешенных чатах)
        if (!commandHandled && isAllowedChat)
            commandHandled = await TryExecuteCommandAsync(text, _userCommands, chatId);

        // Если команда не обработана — вызываем DefaultResponse (только в разрешенных чатах)
        if (!commandHandled && isAllowedChat)
            await DefaultResponse(chatId, username, text);
    }

    /// <summary>
    /// Пытается выполнить команду из словаря
    /// </summary>
    private async Task<bool> TryExecuteCommandAsync(
        string text, 
        Dictionary<string, Func<long, string, Task>> commands, 
        long chatId)
    {
        foreach (var (commandName, handler) in commands)
        {
            string fullCommand = $"/{commandName}";
            if (text.StartsWith(fullCommand + " ", StringComparison.OrdinalIgnoreCase) ||
                text.Equals(fullCommand, StringComparison.OrdinalIgnoreCase))
            {
                string args = text.Length > fullCommand.Length 
                    ? text[(fullCommand.Length + 1)..] 
                    : string.Empty;
                
                await handler(chatId, args);
                return true;
            }
        }
        return false;
    }
}
