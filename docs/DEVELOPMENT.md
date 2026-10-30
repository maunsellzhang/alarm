# 开发与验证说明

本文说明轻提醒的源码组织、构建方式和验证入口。应用功能及日常使用方法见 [README](../README.md)。

## 开发环境

在 Windows 11 上使用 Windows PowerShell。项目通过系统自带的 .NET Framework 编译器 `csc.exe` 构建，目标框架为 .NET Framework 4.8，源代码使用 C# 5 语法。

`build.ps1` 优先查找 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`，缺少该目录中的编译器时再查找 `Framework` 目录。脚本会引用系统的 WPF、Windows Forms、绘图和 JSON 序列化程序集。

编译时，脚本会生成 `Assets/Reminder.ico`，将图标、`UI/Theme.xaml` 和 `UI/MainWindow.xaml` 嵌入 `QingReminder.exe`。构建后的应用不需要旁边再放置这些资源文件。

## 代码职责

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 创建 WPF 应用、加载主题、定位用户数据目录，通过互斥量和系统事件实现单实例唤起 |
| `Core/AlarmEntry.cs` | 提醒记录、模式、状态及保存的倒计时时长 |
| `Core/CountdownInput.cs` | 解析小时、分钟、秒并校验输入范围 |
| `Core/AlarmScheduler.cs` | 新增、编辑、延后、完成提醒，以及到期检查和下一次调度时间 |
| `Core/AlarmStore.cs` | JSON 读写、数据校验、原子替换、备份和损坏数据保留 |
| `UI/MainWindow.cs` | 编辑界面、提醒列表、托盘、调度计时及系统事件响应 |
| `UI/MainWindow.xaml` | 主窗口布局和控件定义 |
| `UI/ReminderWindow.cs` | 到期弹窗、声音、再次提醒、完成及窗口资源释放 |
| `UI/Theme.xaml` | 界面颜色、字体和控件样式 |

## 调整逻辑时需要保留的行为

调度器先准备新的记录列表，再保存到磁盘，保存成功后才更新内存状态并发出变更事件。修改这条调用链时，应保留写入失败后原计划仍有效的行为。

提醒到期后从 `Scheduled` 变为 `Ringing`，重复轮询不会重复触发同一条记录。重新设定或延后后回到 `Scheduled`，完成或删除会移除记录。倒计时原始间隔单独保存在 `CountdownTicks` 中；临时延后 5 分钟应保留原间隔，用户明确修改间隔时才更新它。

主窗口显示时刷新时钟和剩余时间，隐藏或最小化后按最近的提醒时间安排检查；没有待触发提醒时停止后台计时器。关闭和释放窗口时，需要同步解除计时器、系统事件、托盘图标及回调引用。

## 构建与核心测试

```powershell
# 构建应用并执行核心测试
.\build.ps1 -Test

# 只构建应用
.\build.ps1

# 使用独立目录，适合旧版应用仍在运行的场景
.\build.ps1 -Test -OutputDirectory .\.artifacts\local-build
```

如果执行策略阻止脚本，在单次进程中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

核心测试使用临时目录，覆盖时间输入与进位、保存与恢复、重复轮询、重新设定、数据损坏和保存失败等行为。命令返回非零退出码即表示构建或测试失败。

## WPF 界面验证

```powershell
.\verify-ui.ps1
```

该命令会先构建应用，再编译并运行 `Tests/UiTests.cs` 的独立入口。测试会打开实际 WPF 窗口，验证倒计时、指定时间、提醒弹窗、编辑、关闭及重新设定，并将截图和隔离测试数据保存在 `.artifacts/ui-tests`。

已经构建过应用、且生成图标仍存在时，可使用：

```powershell
.\verify-ui.ps1 -SkipBuild
```

## 资源与生命周期验证

首次运行前执行一次 `build.ps1`，确保生成图标存在，然后运行：

```powershell
# 分别测量可见和隐藏状态各 30 秒，并执行生命周期断言
.\verify-resources.ps1

# 快速检查，可见和隐藏状态各测量 5 秒
.\verify-resources.ps1 -IdleSeconds 5
```

参数范围为 5～60 秒，整次运行还包含其他生命周期验证，因此耗时会长于两段采样时间之和。报告写入 `.artifacts/resource-tests/resource-report.txt`。

此测试使用独立数据目录，不执行真实应用入口，也不获取真实应用的单实例互斥量。测试中的强制垃圾回收仅用于检查对象可回收性，不应加入正常应用流程。短时间的 CPU、内存和句柄采样无法证明长期运行始终没有资源泄漏。

## 数据文件与提交范围

正常提醒数据位于 `%LOCALAPPDATA%\QingReminder\reminders.json`，不在源码目录中。修改存储格式时，需要保留历史数据读取以及无法识别的数据不被覆盖的行为，并补充相应回归测试。

提交应包含源代码、XAML、构建脚本、测试代码和说明文档。生成的图标、可执行文件、测试截图、临时提醒数据及本机开发记录均由 `.gitignore` 排除。

提交前检查：

```powershell
git status --short
git diff --check
git diff --cached --check
```

根据改动范围执行相关测试：调度或存储改动执行核心测试，界面改动执行 WPF 集成验证，计时器、事件订阅及窗口释放改动再执行资源验证。
