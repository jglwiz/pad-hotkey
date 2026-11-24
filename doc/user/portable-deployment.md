# 便携部署

## 安装与启动

1. 按 [README](../../README.zh-CN.md) 准备 SDK，运行 `./build.ps1 -Portable`。
2. 将 `local/portable/` 的全部产物复制到自行选择的应用目录。
3. 实际发键需要另行按 HIDMaestro 官方说明配置驱动；应用不会自动安装或升级驱动。
4. 在应用目录运行 `./PadHotkey.exe start`，接受管理员提示，再用 `./PadHotkey.exe status` 核对状态。

自包含 EXE 无需另装 .NET 运行时，但并非 Native AOT，原生依赖可能解包。启动直接启用保存的映射；首次无配置时为空映射。程序不添加开机启动项。需要暂停启动可用 `start --paused true`。

## 数据与升级

配置和令牌默认保存在 `%LOCALAPPDATA%\PadHotkey`，与应用目录分离。升级前使用旧 EXE 的 `stop` 正常退出，再复制新产物；保留用户数据目录。自包含运行时的安全更新需要重新发布程序。

使用 `apply --file <profile-backup 文件路径>` 恢复配置；该操作替换当前配置并产生新修订。卸载时先 stop，再将应用目录移入回收站；用户数据与系统驱动独立保留。

## Skill 与第三方依赖

[skill 源文件](../../skill/pad-hotkey/SKILL.md) 与程序分开部署。按自己的安装位置配置程序路径，不在 skill 中存放 EXE、令牌或个人映射。

本项目 MIT 许可不替代 HIDMaestro 和其他依赖的许可。再分发驱动、SDK 或二进制时，须保留适用的第三方许可证和声明。HIDMaestro 官方发布 ZIP 属于 SDK/工具包，不应假定它是独立的驱动安装程序。

2026-11-24：个人部署路径、映射修订和完整本机操作记录迁入 Git 忽略的 private/；本文只保留通用操作。迁移不会删除此前已公开的 Git 历史。
