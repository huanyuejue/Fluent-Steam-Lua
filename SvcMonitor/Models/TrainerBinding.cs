namespace SvcMonitor.Models;

public class TrainerBinding
{
    public string GameName { get; set; } = string.Empty;
    public string GameExePath { get; set; } = string.Empty;
    public string TrainerFilePath { get; set; } = string.Empty;
    public string TrainerDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
