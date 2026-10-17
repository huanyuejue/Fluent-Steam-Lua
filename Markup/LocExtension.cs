using System.Windows.Data;
using System.Windows.Markup;
using SteamLuaManager.Services;

namespace SteamLuaManager.Markup;

// XAML 用法：Text="{l:Loc Nav_Home}"，语言切换时自动刷新，无需重启。
// 实现是转交一个单向绑定到 LocalizationService 的索引器，刷新靠全局 PropertyChanged。
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
