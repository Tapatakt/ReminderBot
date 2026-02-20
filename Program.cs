using Microsoft.Extensions.Configuration;

namespace ReminderBot;
internal class Program
{
    static async Task Main()
    {
        // Строим конфигурацию: переменные окружения > user-secrets > appsettings.json
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("config.json", optional: true, reloadOnChange: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Читаем настройки из конфигурации
        AppConfig botConfig = configuration.Get<AppConfig>()
            ?? throw new Exception("Не получилось прочитать файл конфигурации");

        Bot bot = new(botConfig.Token, botConfig.NotesFileName, botConfig.AdminId);
        bot.Start();

        Console.WriteLine("Бот запущен.");
        await Task.Delay(-1);
    }
}
