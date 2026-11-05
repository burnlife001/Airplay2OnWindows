# Win11 + .NET 8 控制台最小化到托盘排查

排查日期：2026-11-05

## 结论

| 需求 | 推荐做法 | 不推荐做法 |
|---|---|---|
| 只是把 Windows Terminal 最小化到托盘 | 使用 Windows Terminal 自带的 `minimizeToNotificationArea` | 在 .NET 中拦截控制台窗口消息 |
| 托盘图标、菜单和退出逻辑必须属于当前应用 | 改成 `WinExe + WinForms ApplicationContext`，后台运行 Host；日志用窗口或文件 | 把控制台窗口当作应用主窗口 |
| 必须保留经典控制台窗口 | 使用经典 `conhost`，用 `SetWinEventHook` 监听最小化；托盘与 Hook 放在具有消息循环的 STA 线程 | 在 ConPTY 的伪窗口 HWND 上 Hook `WM_SIZE`、`WM_SYSCOMMAND` 并期望收到真实终端事件 |

## 根因

| 现象 | 原因 |
|---|---|
| 最小化事件收不到，或最小化后没有隐藏 | Win11 常由 Windows Terminal 经 ConPTY 承载控制台。`GetConsoleWindow()` 此时返回伪控制台窗口句柄，不是用户看到的 Terminal 顶层窗口。 |
| 有些 `ShowWindow` 调用又似乎有效 | Windows Terminal 1.14 起增加了兼容层，会把部分针对伪窗口的显示/隐藏调用转发给 Terminal；这不等于该 HWND 已变成真实 Terminal 窗口，也不保证窗口消息 Hook、图标和最小化事件按经典控制台方式工作。 |
| `WM_SETICON` 改不了任务栏图标 | 真实任务栏窗口属于 Windows Terminal，而不是控制台客户端进程。 |
| 托盘图标出现，但点击、菜单或 Hook 没反应 | `NotifyIcon` 和 `SetWinEventHook` 都依赖所在线程的 Windows 消息循环；控制台主线程若被 Host 阻塞，或没有 `Application.Run`，事件无法正常派发。 |
| 在 VS、VS Code、其他终端中表现不同 | 它们也可能使用 ConPTY；问题不属于 Windows Terminal 独有，也不是 .NET 8 的托盘 Bug。 |

## 先做十秒诊断

```csharp
using System.Runtime.InteropServices;
using System.Text;

[DllImport("kernel32.dll")]
static extern nint GetConsoleWindow();

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

var hwnd = GetConsoleWindow();
var className = new StringBuilder(64);
if (hwnd != 0) _ = GetClassName(hwnd, className, className.Capacity);

Console.WriteLine($"HWND=0x{hwnd:X}");
Console.WriteLine($"Class={className}");
Console.WriteLine($"WT_SESSION={Environment.GetEnvironmentVariable("WT_SESSION") is not null}");
```

| 输出 | 含义 | 处理 |
|---|---|---|
| `Class=ConsoleWindowClass` | 经典控制台窗口 | 可沿用 `GetConsoleWindow`，但最小化监听建议改用 WinEvent Hook。 |
| `Class=PseudoConsoleWindow` | ConPTY 伪控制台窗口 | 不要把它当真实 Terminal 窗口 Hook；改用下述方案。 |
| `HWND=0` | 当前没有可供操作的关联控制台窗口 | 不要继续调用窗口 API。 |

窗口类名可用于诊断，但不是稳定的公开契约；`WT_SESSION` 也只适合辅助判断，因为环境变量可能被子进程继承。

## 方案一：只用 Windows Terminal 自带托盘

在 Windows Terminal 的 `settings.json` 顶层加入：

```json
"alwaysShowNotificationIcon": true,
"minimizeToNotificationArea": true
```

效果：最小化窗口后从任务栏隐藏，并转到 Terminal 的通知区域图标。

限制：图标和菜单属于 Windows Terminal，设置是 Terminal 全局行为；它不能替代应用自己的「显示窗口／退出并停止 Host」菜单。

## 方案二：应用拥有自己的托盘图标，推荐

项目文件核心配置：

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWindowsForms>true</UseWindowsForms>
</PropertyGroup>
```

程序结构：

| 部分 | 做法 |
|---|---|
| 主线程 | `[STAThread]`，创建 `ApplicationContext` 和 `NotifyIcon`，最后 `Application.Run(context)`。 |
| Host | 用后台任务运行，不阻塞 UI 线程；退出菜单调用现有停止逻辑和取消令牌。 |
| 显示窗口 | 显示一个真正的 WinForms 日志窗口，或只打开日志文件。 |
| 关闭窗口 | 日志窗口关闭时只隐藏；托盘「退出」才停止 Host 并退出消息循环。 |
| 控制台 | 默认不创建。若只是排障，按需分配；不要让它承担主窗口生命周期。 |

`Application.Run` 提供标准消息循环。若继续使用 `SetWinEventHook`，还应在调用 Hook 的同一线程运行消息循环，并把回调委托保存为字段，防止被 GC 回收。

## 方案三：保留旧控制台代码

| 步骤 | 要求 |
|---|---|
| 运行环境 | 将默认终端改为「Windows 控制台主机（conhost）」再验证。该设置影响用户终端环境，不建议安装程序静默修改。 |
| 最小化监听 | 用 `EVENT_SYSTEM_MINIMIZESTART (0x0016)` 的 `SetWinEventHook`，在回调中筛选目标 HWND，再 `ShowWindow(hwnd, SW_HIDE)`。 |
| 线程 | Hook、`NotifyIcon` 和 `Application.Run` 放在同一个 STA UI 线程。 |
| 回调生命周期 | 保存 `WINEVENTPROC` 委托和 Hook 句柄，退出时 `UnhookWinEvent`。 |
| 兼容降级 | 检测到 ConPTY 或 `HWND=0` 时，不执行旧窗口 Hook 逻辑。 |

不建议通过枚举 `CASCADIA_HOSTING_WINDOW_CLASS` 去操作真实 Terminal 窗口：一个 Terminal 窗口可能包含多个标签页、窗格和进程，隐藏或关闭它会影响并不属于当前应用的会话，而且该实现细节可能变化。

## 资料来源

- [Microsoft Learn：GetConsoleWindow](https://learn.microsoft.com/en-us/windows/console/getconsolewindow)
- [Windows Terminal：ConPTY 显示/隐藏兼容层 PR #12515](https://github.com/microsoft/terminal/pull/12515)
- [Microsoft Learn：Windows Terminal 交互设置](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/interaction)
- [Microsoft Learn：Application.Run](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.run)
- [Microsoft Learn：SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)
- [Microsoft Learn：WinEvent 常量](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants)
- [Microsoft Learn：默认终端策略与 conhost 设置](https://learn.microsoft.com/en-us/windows/terminal/group-policy)
