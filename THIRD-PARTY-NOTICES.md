# 第三方组件声明

## CloudRedirect

- 来源：<https://github.com/Selectively11/CloudRedirect>（构建时自动拉取官方 Release 成品 DLL 并验签，版本钉在 csproj 的 CloudRedirectVersion，按需更新）
- 许可证：MIT License，Copyright (c) 2026 Selectively11，完整文本见上游仓库 LICENSE 文件
- 用途：仅构建其中的 `cloud_redirect.dll`（云存档重定向），嵌入本程序并在用户启用云存档功能时释放到 Steam 目录；其 companion 前端不使用

## SharpCompress

- 来源：<https://github.com/adamhathcock/sharpcompress>（NuGet 包引用 0.50.4，按需更新）
- 许可证：MIT License，完整文本见上游仓库 LICENSE 文件
- 用途：压缩包导入的 7z/rar 解压（zip/tar 走系统内置库）
