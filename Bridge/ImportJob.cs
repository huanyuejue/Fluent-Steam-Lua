using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamLuaManager;

namespace FluentLuaBridge;

/// <summary>
/// 入库行为链中的单个步骤。界面上是按时间顺序追加的行为记录，
/// 不是可回退的进度条，因此用状态标记而不是百分比。
/// </summary>
public enum StepState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped
}

/// <summary>
/// 一条行为链记录。保存翻译键和格式参数，语言切换时刷新显示文字。
/// </summary>
public sealed class ImportStep : ObservableObject
{
    private StepState _state = StepState.Pending;
    private string _detail = string.Empty;
    private string? _detailKey;
    private object?[] _detailArguments = Array.Empty<object?>();

    public ImportStep(string titleKey)
    {
        TitleKey = titleKey;
    }

    public string TitleKey { get; }
    public string Title => L.Get(TitleKey);

    public string Detail
    {
        get => _detailKey == null ? _detail : L.Get(_detailKey,
            _detailArguments.Select(argument => argument is Func<object?> currentValue ? currentValue() : argument).ToArray());
        set
        {
            var wasLocalized = _detailKey != null;
            _detailKey = null;
            _detailArguments = Array.Empty<object?>();
            if (!SetProperty(ref _detail, value) && wasLocalized) OnPropertyChanged(nameof(Detail));
        }
    }

    public void SetLocalizedDetail(string key, params object?[] arguments)
    {
        _detailKey = key;
        _detailArguments = arguments;
        OnPropertyChanged(nameof(Detail));
    }

    public void RefreshLocalizedTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
    }

    public StepState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

/// <summary>
/// 一次入库任务。行为链在开始时就把所有阶段铺好，
/// 执行到哪一步就点亮哪一步，用户能看到整体还有多少没走完。
/// </summary>
public sealed class ImportJob : ObservableObject
{
    private string _displayName = string.Empty;
    private string? _errorMessage;
    private string? _errorKey;
    private object?[] _errorArguments = Array.Empty<object?>();
    public ImportJob(int appId, string displayName)
    {
        Id = Guid.NewGuid().ToString("N");
        AppId = appId;
        DisplayName = displayName;
        CreatedAt = DateTime.Now;
    }

    public string Id { get; }
    public int AppId { get; }
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }
    public DateTime CreatedAt { get; }

    public ObservableCollection<ImportStep> Steps { get; } = new();

    public bool IsFinished { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage
    {
        get => _errorKey == null ? _errorMessage : L.Get(_errorKey, _errorArguments);
        set
        {
            _errorKey = null;
            _errorArguments = Array.Empty<object?>();
            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public void SetLocalizedError(string key, params object?[] arguments)
    {
        _errorKey = key;
        _errorArguments = arguments;
        OnPropertyChanged(nameof(ErrorMessage));
    }

    public void RefreshLocalizedTexts()
    {
        foreach (var step in Steps) step.RefreshLocalizedTexts();
        OnPropertyChanged(nameof(ErrorMessage));
    }
}
