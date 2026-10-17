using System.Globalization;

namespace SteamLuaManager.Services;

public interface IManifestHubKeyService
{
    bool HasSavedKey { get; }
    string? LoadKey();
    bool IsKeyExpired();
    DateTime? GetKeyExpiry();
    string GetKeyStatusText();
    string GetKeyStatusColor();
    (bool Ok, string Message) SaveKey(string? plainKey);
}

public class ManifestHubKeyService : IManifestHubKeyService
{
    // 我与清单监听共用同一份 Key 与填写时间：两边任意填写一次即可，有效期 24 小时
    private static readonly TimeSpan KeyValidity = TimeSpan.FromHours(24);
    private const string KeyEntropy = "ManifestHub";

    private readonly ISettingsService _settingsService;

    public ManifestHubKeyService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool HasSavedKey => !string.IsNullOrEmpty(_settingsService.Load().EncryptedManifestHubKey);

    public string? LoadKey()
    {
        var encrypted = _settingsService.Load().EncryptedManifestHubKey;
        if (string.IsNullOrEmpty(encrypted)) return null;
        return SecureTokenStorage.Unprotect(encrypted, KeyEntropy);
    }

    public bool IsKeyExpired()
    {
        return GetKeyExpiry() is not DateTime expiry || DateTime.UtcNow > expiry;
    }

    public DateTime? GetKeyExpiry()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.EncryptedManifestHubKey)) return null;
        if (!DateTime.TryParse(settings.ManifestHubKeyTime, null, DateTimeStyles.RoundtripKind, out var saved))
            return null;
        return saved.ToUniversalTime().Add(KeyValidity);
    }

    public string GetKeyStatusText()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.EncryptedManifestHubKey))
            return L.Get("Svc_MhkNotFilled");
        var expiry = GetKeyExpiry();
        if (expiry == null)
            return L.Get("Svc_MhkTimeInvalid");
        if (DateTime.UtcNow > expiry)
            return L.Get("Svc_MhkExpired");
        return L.Get("Svc_MhkValidUntil", expiry.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    public string GetKeyStatusColor()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.EncryptedManifestHubKey))
            return "#888888";
        var expiry = GetKeyExpiry();
        if (expiry == null || DateTime.UtcNow > expiry)
            return "#F44336";
        return "#4CAF50";
    }

    public (bool Ok, string Message) SaveKey(string? plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
            return (false, HasSavedKey ? L.Get("Svc_MhkUnchanged") : L.Get("Svc_MhkInputFirst"));
        var encrypted = SecureTokenStorage.Protect(plainKey.Trim(), KeyEntropy);
        if (encrypted == null)
            return (false, L.Get("Svc_MhkSaveFailed"));
        var settings = _settingsService.Load();
        settings.EncryptedManifestHubKey = encrypted;
        settings.ManifestHubKeyTime = DateTime.UtcNow.ToString("o");
        _settingsService.Save(settings);
        return (true, L.Get("Svc_MhkSaved"));
    }
}
