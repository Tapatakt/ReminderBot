namespace ReminderBot;
public class AppConfig
{
    public string Token { get; set; } = string.Empty;
    public long AdminId { get; set; } = 0;
    public string NotesFileName { get; set; } = "notes.txt";
}