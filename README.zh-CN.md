# PadHotkey

[English](README.md) | [简体中文](README.zh-CN.md)

将 Windows 手柄映射为可配置的键盘快捷键，适用于 AI 辅助编程和日常桌面操作。使用 XInput 读取手柄、HIDMaestro 输出虚拟键盘，通过本机 REST API 或 CLI 动态改键。

**当前状态：早期开发版本 v0.1.2。** 首次启动没有预设快捷键；以后启动会直接启用已保存的映射。

## 功能

项目主要希望将手柄变成 **vibe coding 的快捷操作遥控器**：在 AI 编程过程中切换聊天、调整模型推理强度、提交输入、撤销操作，或按住语音输入快捷键。目标应用需要自身支持这些键盘快捷键。PadHotkey 负责发键，不实现语音识别，也不直接控制模型。

映射保持通用且由用户定义。通过 REST API 和 CLI，本地脚本或编程 Agent 可以查询、修改配置，无需改动程序代码。T3 Code 和语音输入配置只是可选示例，不是内置默认值。

- JSON 保存映射；CLI 或 API 修改后立即生效，重启后保留。
- 支持短按、按住、组合键，以及左右修饰键。
- 摇杆和扳机支持触发阈值与独立释放阈值。
- 支持全局映射或按前台进程名限定生效范围。
- 暂停、恢复、限时诊断、最近事件和 SSE 事件流。
- 修改配置时保留备份，通过修订号检查避免覆盖其他客户端的新修改。
- 单进程后台运行，不依赖 WebView 或数据库。

## 运行条件

- Windows x64 和兼容 XInput 的手柄；暂不直接支持其他手柄协议。
- 实际发键需要 HIDMaestro 2.3.0 及其驱动；HID 模式请求管理员权限。
- 源码构建需要 .NET 10 SDK；自包含版不需要另装 .NET 运行时。

应用本身免安装，但虚拟键盘驱动是系统依赖。程序不会静默安装或升级驱动。启动 HID 模式前请退出 AntiMicroX。

## 手柄兼容性

开发使用的实体手柄为 **Q37 Pro，采用 USB 接收器连接**。此前在 AntiMicroX 中，接收器显示为 `Xbox 360 Controller`；这是设备上报的身份，不代表实体型号就是 Xbox 360 手柄。PadHotkey 已在开发机识别到连接的 XInput 槽位，但实体手柄到目标应用的完整流程仍待端到端验证。

兼容依据是 **XInput 协议**，不是品牌白名单。Xbox 系列及第三方手柄，只要其 Windows 驱动和当前连接模式提供标准 XInput 输入，原则上属于支持范围。本项目尚未逐一测试 Xbox 型号及 USB、无线适配器、蓝牙连接方式，因此不宣称所有 Xbox 手柄均已验证兼容。

当前读取一个指定 XInput 槽位（0–3）的 A/B/X/Y、方向键、肩键、Start/Back、摇杆按下、双摇杆和双扳机。Guide、Share 和额外背键暂不提供独立绑定。仅支持 DirectInput 或原始 HID 的设备需要外部 XInput 兼容层，本项目不提供该转换。可通过 status 和限时的 `monitor --seconds 10` 检查实际连接与输入。

## 从源码构建

从 [HIDMaestro 官方 v2.3.0 发布页](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v2.3.0) 获取 SDK，将发行包中的 `HIDMaestro.Core.dll` 及配套依赖 DLL 放入 `local/sdk/`。实际发键所需的驱动按上游说明配置。

在仓库根目录的 PowerShell 中运行：

```powershell
.\build.ps1 -Portable
```

产物为 `local/portable/PadHotkey.exe`，属于 Windows x64 自包含单文件发布，尚非 Native AOT；原生依赖运行时可能解包。已测试的 v0.1.2 EXE 约 129 MiB。SDK 与构建产物均被 Git 忽略。

普通 Release 构建使用 `.\build.ps1`；框架依赖发布使用 `.\build.ps1 -Publish`。直接调用 `dotnet` 时，可用 MSBuild 属性 `HidMaestroSdkPath` 指定 SDK DLL 路径。

## 快速开始

在便携版输出目录执行：

```powershell
.\PadHotkey.exe start
.\PadHotkey.exe status
.\PadHotkey.exe bind --button A --tap Enter
.\PadHotkey.exe bind --button B --hold RightAlt
.\PadHotkey.exe profile
```

HID 模式需接受管理员提示。start 返回不代表服务已经就绪，配置前先用 status 检查。上述映射只是示例，不是默认配置。右 Alt 可能触发目标应用的语音输入等快捷操作。

```powershell
.\PadHotkey.exe pause
.\PadHotkey.exe resume
.\PadHotkey.exe release
.\PadHotkey.exe stop
```

release 同时暂停映射并释放按键。双击 EXE 等同于 start；需要暂停启动时使用 `start --paused true`。程序不会添加开机启动项。

仅测试配置、不发送真实按键时，使用 `start --backend dry-run`。请先停止已有实例；每个 Windows 会话只运行一个实例。使用 `monitor --seconds 10` 可限时查看手柄事件。

## JSON 配置

默认数据目录为 `%LOCALAPPDATA%\PadHotkey`。新建的 profile.json 内容为：

```json
{"version":1,"name":"default","bindings":[]}
```

以下可选示例将右摇杆方向映射为快捷键：

```json
{
  "version": 1,
  "name": "my-shortcuts",
  "bindings": [
    {
      "id": "previous-chat",
      "input": "RS.Right",
      "mode": "tap",
      "keys": ["LeftCtrl", "LeftShift", "BracketLeft"],
      "threshold": 0.9,
      "releaseThreshold": 0.7,
      "processes": []
    }
  ]
}
```

processes 为空表示全局生效，否则填写前台进程名。输入名与键名区分大小写，通过 `GET /v1/schema` 查询支持的名称。请结合手柄握持方向确认实际摇杆输入。

将 JSON 保存为文件后主动导入：

```powershell
.\PadHotkey.exe apply --file .\my-profile.json
```

导入会替换当前配置。version 为并发检查使用的修订号。直接编辑文件不会自动热加载：请显式导入，或停止程序后编辑再启动。修改时保留 `profile-backup-*.json` 备份，恢复同样使用 `apply --file`。

仓库提供 [T3/语音输入可选示例](config/vibe.json)，不会自动导入。

## 本机 REST API

默认地址为 `http://127.0.0.1:17843`。所有请求都需要 Bearer 令牌，位于 `%LOCALAPPDATA%\PadHotkey\api-token.txt`；CLI 自动读取。不要公开该文件。

| 接口 | 用途 |
| --- | --- |
| GET /v1/status | 连接、输入、暂停状态和故障 |
| GET /v1/profile | 当前配置及 ETag |
| PUT /v1/profile | 替换配置，必须带 If-Match |
| PUT /v1/profile/bindings/{id} | 更新单个绑定，必须带 If-Match |
| PUT /v1/runtime | 将 paused 设置为 true 或 false |
| POST /v1/release-all | 暂停并释放按键 |
| POST /v1/shutdown | 释放按键并退出 |
| POST /v1/diagnostics | 暂停时执行限时发键 |
| GET /v1/events | 最近事件 |
| GET /v1/events/stream | 面向本地客户端的 SSE 事件流 |
| GET /v1/schema | 支持的键名与动作，不是标准 JSON Schema |

写入前读取配置的 ETag，并作为 If-Match 发送。旧修订号返回 HTTP 412，应重新读取并合并修改后重试。服务仅监听回环地址，拒绝带 Origin 的请求，面向本地工具而非浏览器页面。

## 开发与验证

运行映射引擎测试：

```powershell
dotnet run --project tests/PadHotkey.Tests/PadHotkey.Tests.csproj -c Release
```

Python API、配置生命周期和退出检查位于 tests/，参数见各脚本的 `--help`。

v0.1.2 已记录通过 20 项引擎检查、17 项 API 检查、9 项生命周期检查和 3 项退出检查。开发机实测观察到真实 HID 的 F13 按下与释放。**实体手柄到目标应用的完整流程，以及本版本的语音输入效果，尚未端到端验证。**

当前仅支持一个活动配置和一个手柄索引，使用 US HID 键位编码；暂无托盘界面和内置开机启动。强制结束进程后的行为尚未验证；正常暂停、断连和退出路径会释放按键。

## 文档与 Agent 集成

- [使用与 API](doc/user/usage.md)
- [设计说明](doc/dev/design.md)
- [验证历史](doc/dev/verification.md)
- [便携部署记录](doc/user/portable-deployment.md)：包含开发机路径和实测状态，不是所有机器的安装默认值。
- [可选 Codex skill](skill/pad-hotkey/SKILL.md)：安装前按实际位置调整 EXE 路径；skill 与程序分开部署。

反馈问题时请提供程序版本、Windows 版本、手柄型号、backend 和复现步骤。请移除 API 令牌及个人配置数据。

## 许可证

项目尚未选择许可证，目前正在准备开源发布，尚未授予开源许可。HIDMaestro 等依赖保留各自许可证。公开发布二进制前，需要确定项目许可证并检查第三方再分发声明。
