using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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
/// 一条行为链记录。文案在构造时定稿，后续只改状态，
/// 避免界面刷新时文字跳动。
/// </summary>
public sealed class ImportStep : ObservableObject
{
    private StepState _state = StepState.Pending;
    private string _detail = string.Empty;

    public ImportStep(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
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
public sealed class ImportJob
{
    public ImportJob(int appId, string displayName)
    {
        Id = Guid.NewGuid().ToString("N");
        AppId = appId;
        DisplayName = displayName;
        CreatedAt = DateTime.Now;
    }

    public string Id { get; }
    public int AppId { get; }
    public string DisplayName { get; set; }
    public DateTime CreatedAt { get; }

    public ObservableCollection<ImportStep> Steps { get; } = new();

    public bool IsFinished { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
