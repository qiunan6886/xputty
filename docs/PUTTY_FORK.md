# PuTTY fork 改动说明（xPuTTY）

基线：PuTTY 0.85（官方源码 tarball，2026-08-16）。所有改动限于 `putty/windows/` 下，未触碰 SSH/Telnet/串口协议实现与终端仿真核心。

## 1. 新增文件

### `windows/xputty.h` / `windows/xputty.c`

xPuTTY 外壳与终端进程之间的胶水层：

| 功能 | 说明 |
| --- | --- |
| `-xputty-embed <hwnd>` | 启动后把终端窗口重设为无边框 `WS_CHILD`，`SetParent` 到外壳的窗格面板并铺满 |
| `-xputty-host <hwnd>` | 接收通知的外壳窗口 |
| `-xputty-theme <file>` | 解析主题 INI：字体、字号、回滚行数、光标样式、22 个调色板颜色（前/背景、光标、ANSI16），在 `conf` 初始化后应用 |
| 消息 `xPuTTYCreated` | `PostMessage(host, wParam=terminal HWND)`，外壳据此绑定窗格 |
| 消息 `xPuTTYSessionExit` | 会话结束/窗口关闭时通知外壳（wParam=退出码） |
| 消息 `xPuTTYHotkey` | 把 Ctrl+Shift+T/W/E/D/M、Ctrl+Tab、F11、Ctrl+Shift+1..9 转发给外壳并吞掉按键 |
| 消息 `xPuTTYFatal` | 嵌入模式下把 `modalfatalbox`/`nonfatal` 的错误文本同步发给外壳，不弹系统 MessageBox |
| `WM_COPYDATA`（magic `'XPTY'`） | 外壳注入宏文本：`4 字节 flags + UTF-8`，按“逐字符键入”方式送入终端（新增行归一为 CR） |
| `clear_full_screen` 嵌入模式直接返回 | 否则 `WM_SIZE` 会把标题栏/边框加回子窗口 |
| `WM_CLOSE` / 退出路径 | 嵌入模式不再弹“确认关闭”，直接销毁并通知外壳 |
| 上下文菜单 | 嵌入模式删除 New/Duplicate Session、Saved Sessions、Change Settings、Full Screen 等会产生额外顶层窗口的菜单项 |

主题 INI 示例（shell 侧 `AppContext.BuildThemeIni()` 生成）：

```ini
[terminal]
font=Cascadia Mono
font-size=10
scrollback=8000
cursor=block
bell=off
[colors]
foreground=#C3CCD9
foreground-bold=#EDF1F7
background=#0B0D11
cursor-text=#0B0D11
cursor-colour=#C3CCD9
ansi0=#1A1D23
...
ansi15=#EDF1F7
```

## 2. 修改的既有文件

| 文件 | 改动 |
| --- | --- |
| `windows/putty.c` | `gui_term_process_cmdline()` 参数循环中，在通用解析前拦截 `-xputty-*` 参数 |
| `windows/window.c` | `#include "xputty.h"`；WinMain 注册消息、应用主题；窗口创建后调用 `xputty_attach_window()` 完成嵌入；`WM_COPYDATA` 注入处理；`WM_KEYDOWN` 热键转发；`WM_CLOSE`/`WM_DESTROY` 嵌入分支；`exit_callback()` 嵌入分支不弹框改为通知；`modalfatalbox()`/`nonfatal()` 路由到外壳；`clear_full_screen()` 嵌入模式短路 |
| `windows/CMakeLists.txt` | `putty`/`puttytel`/`pterm` 目标加入 `xputty.c` |

嵌入的实现方式说明：**不是**在 `CreateWindowEx` 时直接建 `WS_CHILD`，而是先按原生方式创建顶层窗口、完成终端初始化（`term_init`、字体、菜单、后端启动），在 `ShowWindow` 之前再 `SetWindowLongPtr(GWL_STYLE)` + `SetParent` + `SetWindowPos` 重设为子窗口。原因：若创建时即为可见子窗口，`CreateWindowEx` 期间会派发 `WM_SIZE`/`WM_MOVE`，此时 `WinGuiSeat` 尚未完成初始化（`GWLP_USERDATA` 未设、`term` 为 NULL）导致崩溃。

## 3. 调试

设置环境变量 `XPUTTY_DEBUG=<日志文件路径>` 后，xputty.c 会把命令行解析、主题应用、窗口挂接、通知等关键步骤写入日志（`xputty_trace`）。正常使用无需设置。

## 4. 已知差异 / 限制

- 嵌入模式下 PuTTY 的原生“更改设置”对话框仍可打开，但保存后的部分窗口类设置需重开会话生效。
- 主机密钥确认、私钥口令等 PuTTY 原生对话框仍以顶层对话框形式出现（居父窗口之上），未做深色主题化。
- 未改动 PuTTY 的 `-cleanup`、Pageant、PuTTYgen 等行为。
