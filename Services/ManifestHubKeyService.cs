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
            return "未填写 API Key";
        var expiry = GetKeyExpiry();
        if (expiry == null)
            return "Key 时间无效，请重新填写";
        if (DateTime.UtcNow > expiry)
            return "api key 已过期，请重新获取填入后使用";
        return $"有效期至 {expiry.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
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
            return (false, HasSavedKey ? "Key 未改动" : "请先输入 API Key");
        var encrypted = SecureTokenStorage.Protect(plainKey.Trim(), KeyEntropy);
        if (encrypted == null)
            return (false, "Key 保存失败（DPAPI 不可用）");
        var settings = _settingsService.Load();
        settings.EncryptedManifestHubKey = encrypted;
        settings.ManifestHubKeyTime = DateTime.UtcNow.ToString("o");
        _settingsService.Save(settings);
        return (true, "API Key 已保存，有效期 24 小时");
    }
}
