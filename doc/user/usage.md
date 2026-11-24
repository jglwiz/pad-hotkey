# PadHotkey 使用

适用 Windows x64 和已有 HIDMaestro 2.3.0 驱动。自包含 EXE 已附带 .NET 10 运行时；构建才需要 SDK。HID 模式需管理员权限，dry-run 不发键。不要同时运行 AntiMicroX。程序不添加开机启动项。

## 构建和启动

本机已部署到 `D:\app\PadHotkey`，双击 EXE 或使用以下命令：

```powershell
& 'D:\app\PadHotkey\PadHotkey.exe' start
& 'D:\app\PadHotkey\PadHotkey.exe' status
```

开发时在项目根执行：

```powershell
.\build.ps1 -Portable
.\start.ps1
.\padhotkey.ps1 status
```

启动直接启用已有 JSON 映射，缺少配置则创建空映射；检查 status 中的 backend、connected、index、paused、fault。诊断时可用 `PadHotkey.exe start --paused true` 启动为暂停。通过 `start.ps1 -DryRun` 试配置时不会发送真实按键。`start.ps1 -Seconds 60` 会在一分钟后自动退出。项目脚本优先使用 local/portable，找不到时使用 local/publish；以下 CLI 示例也可改用部署目录中的 EXE。

运行配置与令牌默认保存在 `%LOCALAPPDATA%\PadHotkey`。CLI 自动读取令牌。构建需把官方发行包的 `HIDMaestro.Core.dll` 放到 `local/sdk/`，或传 MSBuild 属性 `HidMaestroSdkPath`。本机已配置；换机器需要另行准备 SDK 与驱动。

```powershell
.\padhotkey.ps1 bind --button B --hold RightAlt
.\padhotkey.ps1 bind --button B --tap LeftCtrl+F10
.\padhotkey.ps1 pause
.\padhotkey.ps1 release
.\padhotkey.ps1 stop
.\padhotkey.ps1 monitor --seconds 10
```

`release` 同时暂停和释放；重新启用用 resume。短按 Ctrl+F10 只是发送一次快捷键，工具不读取豆包录音状态。hold RightAlt 跟随按钮按住/松开。键名称区分大小写；可读 `/v1/schema` 查询。

可选 config/vibe.json 示例中 A=Enter、X=Tab、Y=左 Ctrl+Z；B 全局按住右 Alt。示例的其他键只在 T3 Code / T3 Code (Beta) 前台启用。根据旧 AntiMicroX 第二根摇杆的四方向配置迁移到右摇杆：原始 `RS.Right` 90% 为上一聊天、`RS.Left` 90% 为下一聊天；`RS.Up` 60% 提高 effort、`RS.Down` 60% 降低 effort；LT/RT 切聊天。原配置把第二根摇杆原始向右注释为竖握向上，原始向左注释为竖握向下。本轮没有实推手柄核验方向，如不一致，读取 inputs 后交换绑定的 input。聊天释放阈值为 70%，effort 为 40%。

## JSON 配置

首次创建 profile.json 时内容为空映射：

```json
{"version":1,"name":"default","bindings":[]}
```

name 是自定义标签，API 路径固定为 `/v1/profile`，目前只管理单个当前配置。version 为并发控制修订号。首次运行没有任何快捷键，需要 bind、API 配置或主动导入示例：

```powershell
.\padhotkey.ps1 apply --file .\config\vibe.json
```

REST/CLI 修改会校验、持久化并立即应用。直接编辑 JSON 不会自动热加载；可 apply 显式导入，或停止程序后编辑再重启。完整 PUT 将 bindings 设为 `[]` 可清空映射，并保留旧版本备份。

升级保留已有用户 JSON；旧版自动生成的个人映射也不会被清空。首次为空仅适用于缺少 profile.json 的目录。旧 `/v1/profiles/vibe` 已移除，调用方改用 `/v1/profile`，CLI 已同步更新。

## REST API

地址 `http://127.0.0.1:17843`，每次请求需 `Authorization: Bearer <本机 api-token.txt 内容>`。不要将令牌写入可提交文件。

| 请求 | 用途 |
| --- | --- |
| GET /v1/status | 连接、索引、前台进程、输入、暂停和错误 |
| GET /v1/profile | 当前配置；响应 ETag 即修订号 |
| PUT /v1/profile | 完整替换；必须带 If-Match |
| PUT /v1/profile/bindings/b | 更新一个绑定；必须带 If-Match |
| PUT /v1/runtime | `{"paused":true}` 或 false |
| POST /v1/release-all | 取消诊断、暂停并释放 |
| POST /v1/shutdown | 释放按键并正常退出服务 |
| POST /v1/diagnostics | 暂停时执行有时限的虚拟键盘诊断 |
| GET /v1/events | 最近 200 条事件，支持 after 序号 |
| GET /v1/events/stream | SSE，浏览器 Origin 请求被拒绝，供本地客户端使用 |
| GET /v1/schema | 键名与动作能力、示例配置；不是标准 JSON Schema |

更新 B 的 JSON 请求体：

```json
{"id":"b","input":"B","mode":"hold","keys":["RightAlt"],"threshold":0.9,"releaseThreshold":0.7,"processes":[]}
```

processes 为空表示全局，否则为不含路径的进程名（可带 .exe）。版本过期返回 412，非法配置返回 400，冲突操作返回 409。修改前 GET 取得 ETag，写入后回读确认。`profile-backup-*.json` 保留旧版本；恢复可用 `apply --file <备份文件>`，会产生新修订号。暂停后恢复，避免半途按键状态。

## 限时诊断

```powershell
.\padhotkey.ps1 pause
.\padhotkey.ps1 diagnose --tap F13 --seconds 1
.\padhotkey.ps1 diagnose --hold RightAlt --seconds 5
```

诊断会在三秒准备时间后发键并自动释放，CLI 等待结束后输出状态和事件。右 Alt 可能开启麦克风；测试前将焦点放到可输入文字的位置。无需在测试过程中回复确认。诊断完成仍保持暂停。API 可指定 delayMs 与 durationMs，最长各 15 秒；tap durationMs 至少 400ms。

停止程序用 `stop`；`serve --seconds N` 支持自动正常退出。程序退出不卸载 HIDMaestro 驱动。保留旧 AntiMicroX 配置，回退时先停止本工具再手动启动旧方案。重新构建前先 stop，防止运行进程锁住 DLL。
