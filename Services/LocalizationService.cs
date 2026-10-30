using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SteamLuaManager.Services;

// 应用内多语言服务：首次启动跟随系统显示语言，词库内嵌 JSON。
// 加新语言三步：1) 照抄 Resources/Lang/en.json 翻译一份（如 ja.json）；
// 2) 下方 SupportedLanguages 加一行；3) 设置页下拉框自动出现，无需改其他代码。
// 切换语言实时生效（触发全局绑定刷新），不重启；线程文化保持不动，避免影响数字日期解析。
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    // 显示名固定用该语言自称，不随当前语言变化，方便用户认出
    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages = new[]
    {
        new LanguageOption("zh", "中文"),
        new LanguageOption("en", "English"),
    };

    private const string FallbackLanguage = "zh";

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _cache = new();
    private readonly object _lock = new();

    private string _currentLanguage = GetSystemLanguage();

    private LocalizationService() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (_currentLanguage == value) return;
            _currentLanguage = value;
            OnPropertyChanged();
            // 空属性名=全部 indexer 绑定刷新，各页面文字即时切换
            OnPropertyChanged(string.Empty);
        }
    }

    // XAML {Loc Key} 绑定的就是这个索引器
    public string this[string key] => GetString(key);

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        SupportedLanguages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    // Use the Windows display language, without changing number/date formatting.
    // All Chinese locales share the Chinese translation; other locales use English.
    public static string GetSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";

    public static string Normalize(string? code)
    {
        var hit = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return hit?.Code ?? GetSystemLanguage();
    }

    public void SetLanguage(string? code)
    {
        code = Normalize(code);
        // 先加载词库再切语言，避免切换瞬间大面积回退显示 key
        _ = GetDictionary(code);
        CurrentLanguage = code;
    }

    public string GetString(string key)
    {
        if (GetDictionary(CurrentLanguage).TryGetValue(key, out var v))
            return v;
        if (!CurrentLanguage.Equals(FallbackLanguage, StringComparison.OrdinalIgnoreCase) &&
            GetDictionary(FallbackLanguage).TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    public string GetString(string key, params object?[] args)
    {
        var template = GetString(key);
        if (args == null || args.Length == 0) return template;
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private IReadOnlyDictionary<string, string> GetDictionary(string code)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(code, out var cached))
                return cached;
            var loaded = LoadFromResource(code) ?? LoadFromResource(FallbackLanguage) ??
                         new Dictionary<string, string>();
            _cache[code] = loaded;
            return loaded;
        }
    }

    private static IReadOnlyDictionary<string, string>? LoadFromResource(string code)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = $"SteamLuaManager.Resources.Lang.{code}.json";
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            return dict;
        }
        catch
        {
            return null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record LanguageOption(string Code, string Display);
