namespace SteamLuaManager.Services;

/// <summary>手柄输入服务：通过 XInput 轮询驱动焦点导航，提供按钮边沿触发与摇杆方向导航。</summary>
public interface IGamepadService
{
    /// <summary>启动后台轮询（已启动则空操作）。</summary>
    void Start();

    /// <summary>停止轮询并释放线程。</summary>
    void Stop();

    /// <summary>设置导航灵敏度倍率（0.5~2.0），影响重复触发间隔。</summary>
    void SetSensitivity(double sensitivity);

    /// <summary>是否正在轮询。</summary>
    bool IsRunning { get; }
}

/// <summary>Y 键：切换导航栏展开/折叠。</summary>
public record GamepadTogglePaneMessage;

/// <summary>Start/Back 键：导航到指定页面（"Settings"/"About"）。</summary>
public record GamepadNavigateMessage(string Target);

/// <summary>LT 键：左侧主导航切换到上一页（循环）。</summary>
public record GamepadPrevPageMessage;

/// <summary>RT 键：左侧主导航切换到下一页（循环）。</summary>
public record GamepadNextPageMessage;
