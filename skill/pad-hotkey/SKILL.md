---
name: pad-hotkey
description: 通过本机 PadHotkey EXE 和 REST API 查询手柄状态、配置 JSON 按键映射、暂停恢复及执行限时诊断。用于 Windows 手柄快捷键配置，不用于修改目标应用自身的快捷键设置。
---

# PadHotkey

程序位于 `D:\app\PadHotkey\PadHotkey.exe`。用户配置与 API 令牌位于 `%LOCALAPPDATA%\PadHotkey`；不要打印或复制令牌。程序与 skill 分开部署，不在 skill 中存放 EXE 或用户映射。

先运行 `PadHotkey.exe status` 检查 backend、connected、index、paused、fault。连接失败时先检查程序是否运行，不把连接失败当作需要重装驱动。用户要求启动时使用 `PadHotkey.exe start`，该入口请求管理员权限并隐藏后台窗口；随后轮询 status 确认已就绪。启动直接启用保存的映射；无配置时为空映射，不自行导入个人配置。

## 配置

先 `profile` 读取，修改用户指定部分，再回读确认。简单单键修改使用 CLI：

```powershell
& 'D:\app\PadHotkey\PadHotkey.exe' bind --button B --hold RightAlt
& 'D:\app\PadHotkey\PadHotkey.exe' bind --button B --tap LeftCtrl+F10
& 'D:\app\PadHotkey\PadHotkey.exe' pause
& 'D:\app\PadHotkey\PadHotkey.exe' resume
& 'D:\app\PadHotkey\PadHotkey.exe' stop
```

以上仅是调用示例，不是默认映射。完整 JSON 用 `apply --file <路径>` 显式导入。不要直接编辑运行中的 profile.json 并假定热加载。

复杂映射用 REST：`http://127.0.0.1:17843`，Authorization Bearer 从本机令牌文件读取。GET `/v1/profile` 获取 ETag；PUT `/v1/profile` 或 `/v1/profile/bindings/{id}` 带 If-Match，412 时重新读取并合并，不能盲目覆盖。

绑定字段：id、input、mode（tap/hold）、keys、threshold、releaseThreshold、processes。可用输入与键名从 GET `/v1/schema` 查询，区分 LeftCtrl/RightCtrl、LeftAlt/RightAlt；processes 空数组为全局，否则是可带 .exe 的进程名。摇杆阈值 0..1，释放阈值必须低于触发阈值。先查实际输入，再判断竖握方向。

## 验证与恢复

`monitor --seconds 10` 自动结束，不要求用户在监测中回复。诊断先 pause，再 `diagnose --tap F13 --seconds 1`；真实 HID 会发键，语音快捷键会触发麦克风，按用户要求确定目标与测试键。诊断结束保持暂停；只在用户期望启用映射时 resume。不要把日志发键成功说成目标应用成功。

`release` 立即暂停并释放；`stop` 正常释放并退出。配置更新自动保留 profile-backup-*.json；恢复旧配置用 apply，不删除备份。HID 模式拒绝与 AntiMicroX 同时运行。

驱动缺失时查看安装目录的 `DRIVER.md` 和 `drivers/`。安装驱动会修改系统证书和驱动注册，不能因普通配置请求就自动安装或升级。程序缺失时报告未部署，不能改用其他程序代替。
