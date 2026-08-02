# Fluent Steam Lua 管理工具

基于 WPF + Fluent Design 开发的现代化轻量级 Steam Lua 入库管理工具，目前仅适配OpenSteamTool

> 🌐 项目介绍页：https://huanyuejue.github.io/Fluent-Steam-Lua/

## 预览

<table>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/huanyuejue/Fluent-Steam-Lua/assets/screenshots/home.gif" width="400" />
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/huanyuejue/Fluent-Steam-Lua/assets/screenshots/ruku.gif" width="400" />
    </td>
  </tr>
  <tr>
    <td align="center">
      <img src="https://raw.githubusercontent.com/huanyuejue/Fluent-Steam-Lua/assets/screenshots/setting.gif" width="400" />
    </td>
    <td align="center">
      <img src="https://raw.githubusercontent.com/huanyuejue/Fluent-Steam-Lua/assets/screenshots/dlc.gif" width="400" />
    </td>
  </tr>
</table>

## 功能特性

#### 入库管理
- 已入库的游戏自动读取并展示封面和中文游戏名（三种布局展示自由切换）
- 搜索预览并入库新游戏（支持 AppId / 游戏名）
- 查询当前游戏清单的DLC入库情况
- 从 Steam 正版账号中提取已拥有游戏的Lua清单和Bin成就文件
- 快速禁用或启用 Lua 清单的游戏入库状态，无需删除 Lua 清单
- 支持固定游戏清单版本到最新版本或当前已安装版本

#### 修改器
- 搜索游戏的修改器并下载管理（风灵月影修改器）
- 修改器支持绑定游戏进程，以达到开启游戏自动启动修改器
- 绑定后支持开启修改器的同时自动激活修改器的指定功能项

#### 内核
- 目前只适配 OpenSteamTool
- 支持快捷 安装/更新/卸载 OpenSteamTool 内核
- 支持自定义 Lua 清单文件的扫描存放位置

#### Steam
- 快捷启动和重启Steam程序
- 快捷切换登录本地已有凭证的Steam账号
- 一键管理当前库里已拥有游戏的成就数据并保存云端（解锁/回锁）

#### 特性
- 文件变更自动监控并刷新缓存
- 基于 Fluent Design 的现代化界面，使用WPF编译，方便调试
- 主题支持 跟随系统/深色模式/浅色模式
- 背景色支持 亚克力/云母/无效果
- 支持自动检测更新（可关闭）


## 系统要求

- Windows 10 1809+ / Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## 构建方法

发布为单文件可执行程序：

```bash
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true /p:DebugType=None /p:DebugFullType=None
```

发布为散文件可执行程序：

```bash
dotnet publish -c Release -r win-x64
```
