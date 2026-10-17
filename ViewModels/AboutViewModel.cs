using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

// 常见问题窗口的数据：问题列表 + 关键字过滤；VM 无外部依赖，由窗口直接持有
public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchText = string.Empty;

    public ObservableCollection<FaqItem> FilteredFaqs { get; } = new();

    private readonly List<FaqItem> _allFaqs = BuildFaqs();

    private static List<FaqItem> BuildFaqs() => new()
    {
        new(L.Get("Faq_Q1"), L.Get("Faq_A1")),
        new(L.Get("Faq_Q2"), L.Get("Faq_A2")),
        new(L.Get("Faq_Q3"), L.Get("Faq_A3")),
        new(L.Get("Faq_Q4"), L.Get("Faq_A4")),
        new(L.Get("Faq_Q5"), L.Get("Faq_A5")),
        new(L.Get("Faq_Q6"), L.Get("Faq_A6")),
        new(L.Get("Faq_Q7"), L.Get("Faq_A7")),
        new(L.Get("Faq_Q8"), L.Get("Faq_A8")),
        new(L.Get("Faq_Q9"), L.Get("Faq_A9")),
        new(L.Get("Faq_Q10"), L.Get("Faq_A10")),
        new(L.Get("Faq_Q11"), L.Get("Faq_A11")),
        new(L.Get("Faq_Q12"), L.Get("Faq_A12")),
        new(L.Get("Faq_Q13"), L.Get("Faq_A13")),
        new(L.Get("Faq_Q14"), L.Get("Faq_A14")),
        new(L.Get("Faq_Q15"), L.Get("Faq_A15")),
        new(L.Get("Faq_Q16"), L.Get("Faq_A16")),
    };

    public AboutViewModel()
    {
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        RefreshFilteredFaqs();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _allFaqs.Clear();
        _allFaqs.AddRange(BuildFaqs());
        RefreshFilteredFaqs();
    }

    partial void OnSearchTextChanged(string value) => RefreshFilteredFaqs();

    private void RefreshFilteredFaqs()
    {
        FilteredFaqs.Clear();
        var q = SearchText?.Trim();
        foreach (var item in _allFaqs)
        {
            if (string.IsNullOrEmpty(q)
                || item.Question.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Answer.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredFaqs.Add(item);
        }
    }
}
