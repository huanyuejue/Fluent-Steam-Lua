# 第三方组件声明

## CloudRedirect

- 来源：原作者已删库，现为保留 fork <https://github.com/huanyuejue/CloudRedirect>（构建时自动拉取自家保留仓 Release 成品 DLL 并验签，默认跟随最新 Release，可用 csproj 的 CloudRedirectVersion 固定版本）
- 许可证：MIT License，Copyright (c) 2026 Selectively11（原作者），完整文本见保留仓 LICENSE 文件
- 用途：仅构建其中的 `cloud_redirect.dll`（云存档重定向），嵌入本程序并在用户启用云存档功能时释放到 Steam 目录；其 companion 前端不使用

## SharpCompress

- 来源：<https://github.com/adamhathcock/sharpcompress>（NuGet 包引用 0.50.4，按需更新）
- 许可证：MIT License，完整文本见上游仓库 LICENSE 文件
- 用途：压缩包导入的 7z/rar 解压（zip/tar 走系统内置库）

## SteamAchievementManager（成就功能）

- 来源：<https://github.com/gibbed/SteamAchievementManager>（`Services/SamApi/` 内嵌其 API 绑定源码共 31 个文件：Callbacks/Interfaces/Types/Wrappers，未改动；完整许可证文本见 `Services/SamApi/LICENSE.txt`）
- 许可证：zlib License，Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
- 用途：成就管理（解锁/回锁）读写 Steam 客户端成就数据；其 GUI 程序未使用

## Steam-auto-crack（免启动功能）

- 来源：<https://github.com/SteamAutoCracks/Steam-auto-crack>（`CrackTool/` 内 vendor 源码：Core＋Steamless.API＋7 个解包器，已改目标 net8.0-windows/x64 跟随主程序编译；ValveKeyValue 依赖以自研 KV1 解析替代）
- 许可证：MIT License，Copyright 归上游作者所有，完整文本见上游仓库 LICENSE.md 文件
- 用途：SteamStub 脱壳＋Goldberg 模拟器部署（仅支持 SteamStub 游戏，Denuvo 无效）；其 GUI/CLI 项目未使用
