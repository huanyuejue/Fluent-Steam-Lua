using System.IO;
using System.Text.Json;
using System.Windows;
using SteamLuaManager.Services;

namespace FluentLuaBridge;

/// <summary>Use the main app's saved language even when the bridge is deployed elsewhere.</summary>
internal static class BridgeLocalization
{
    public const string ConfigFileName = "bridge-language.json";

    public static string Language => LocalizationService.Instance.CurrentLanguage;

    public static void Refresh()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        var settingsPath = ReadString(configPath, "settingsFile")
                           ?? Environment.GetEnvironmentVariable("FLUENT_SETTINGS_FILE");
        var language = ReadString(settingsPath, "Language");
        if (!LocalizationService.IsSupported(language))
            language = ReadString(configPath, "language");
        if (!LocalizationService.IsSupported(language))
            language = Environment.GetEnvironmentVariable("FLUENT_LANGUAGE");
        language = LocalizationService.Normalize(language);

        // The shared localization service raises WPF binding notifications.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => LocalizationService.Instance.SetLanguage(language));
        else
            LocalizationService.Instance.SetLanguage(language);
    }

    private static string? ReadString(string? path, string property)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch
        {
            // A settings write can overlap a health request; retain the persisted fallback.
            return null;
        }
    }
}
