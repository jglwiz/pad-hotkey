# 便携部署

2026-11-24 用户要求部署至 `D:\app\PadHotkey`，先使用项目本地 skill 测试，通过后移入回收站，再部署全局 skill。

选用 Windows x64 自包含发布，将 .NET 运行时随程序发布；不要求目标机器安装开发 SDK。单文件 EXE 可以封装托管与原生依赖，但原生依赖运行时可能解包缓存。自包含运行时的安全更新需要重新发布程序，不自动使用机器上更新的共享运行时。

HIDMaestro 2.3.0 驱动仍是系统级前提，因此“绿色版”只指应用本身免安装，不代表不使用驱动或不修改系统。已附带官方发布 ZIP、来源和 SHA-256，用户无需重复上网找包；已有驱动不重装。主程序缺驱动时提示，不静默安装或升级。

程序目录和 skill 分离；配置及令牌继续使用 `%LOCALAPPDATA%\PadHotkey`。启动直接加载并启用已有 JSON，首次空配置仍无快捷键。此部署任务不添加登录启动项。

## 安装结果（2026-11-24）

- 主程序：`D:\app\PadHotkey\PadHotkey.exe`，v0.1.2，135,382,189 字节，约 129.1 MiB。包含 .NET 运行时，无需另装 SDK 或共享运行时；尚非 Native AOT。
- 驱动包：`D:\app\PadHotkey\drivers\HIDMaestro-v2.3.0.zip`；来源、散列与安装边界见同目录上级的 `DRIVER.md`。这是 SDK/工具包，驱动载荷内嵌 DLL，并非独立 Setup.exe。本次未重装驱动。
- 全局 skill：`C:\Users\J\.codex\skills\pad-hotkey\SKILL.md`。本地 skill 已通过 CLI 行为测试及格式检查，随后移入回收站；项目保留 [skill 源文件](../../skill/pad-hotkey/SKILL.md)，不在项目技能发现目录内。
- 当前用户的 profile.json 修订 3、10 条绑定保持原样，部署前后 SHA-256 相同。真实 HID 已启动，手柄索引 0 连接，paused=false、fault=null。

双击 EXE 或运行 `PadHotkey.exe start` 会请求管理员权限并隐藏后台窗口。用 `status` 确认就绪，`pause` 暂停，`resume` 恢复，`stop` 正常退出。需要启动时暂停可用 `start --paused true`。重新启动后仍默认启用保存的映射，输入先复位再触发。

构建使用项目根目录 `./build.ps1 -Portable`，产物位于 `local/portable/`；升级先用旧 EXE 的 stop 退出，再复制新产物，保留用户数据目录。回退可重新部署先前版本；恢复映射用 `apply --file <profile-backup-*.json>`。卸载时先 stop，将应用与 skill 目录移入回收站；用户数据和系统驱动独立保留，按需另行处理。

验证：20 项引擎、17 项 API、9 项配置生命周期、3 项退出检查通过；本地 skill 的启动、改键回读、暂停、限时诊断、恢复和退出通过；真实 HID F13 按下/释放通过。未验证本版本的豆包语音及实体手柄端到端行为。全局 skill 需由新会话发现；当前会话的技能列表可能仍显示原本地路径。
