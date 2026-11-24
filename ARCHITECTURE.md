# xPuTTY 架构设计（v1）

## 1. 目标

做一个 Windows 专用的 SSH / 串口终端管理器，对标 MobaXterm 的使用习惯，但：

- **宏（Macro）数量不受限**：默认自带多个宏按钮，可通过「宏管理」弹窗新增 / 编辑 / 删除 / 拖拽排序 / 设快捷键 / 选颜色 / 设置是否自动回车。
- **分屏（多开）不受限**：任意横向 / 纵向切分，支持 2x2、1x3、1x4 等任意树形布局，可分屏 4 个以上窗格，也可多标签页同时多开会话。
- **界面现代化**：深色（可选浅色）主题、自定义标题栏、会话侧栏、标签栏、宏工具条、状态栏，视觉参考 Dribbble 上的现代终端 / SSH 客户端作品（Tabby、Warp、Termius、MobaXterm、Windows Terminal）。
- **基于 PuTTY 源码改造**，最终交付 **MSI 安装包**，仅支持 x64 Windows 10/11。

## 2. 总体架构

采用「**PuTTY 内核 fork + 现代化 WinForms 外壳**」的双进程架构：

```
xputty.exe  (C# / .NET Framework 4.8, WinForms 自绘 UI, 单实例)
 ├─ 主窗口 (无边框 + DWM 圆角/阴影)
 │   ├─ 自定义标题栏 / 标签栏 / 会话侧栏 / 宏工具条 / 状态栏
 │   └─ 分屏树 (SplitTree)：每个叶子 Pane 是一个宿主 HWND
 │        └─ 每个 Pane 内嵌一个 putty.exe 子窗口（WS_CHILD 跨进程 SetParent/创建）
 │
 └─ 启动/管理: putty.exe (PuTTY 0.85 fork)
      putty.exe -xputty-embed <paneHWND> -xputty-host <shellHWND> -xputty-theme <theme.ini> ...
      ├─ 以 WS_CHILD 直接创建终端窗口（嵌入模式）
      ├─ 主题 INI 覆盖字体/前景/背景/ANSI16 色/光标/回滚
      ├─ 自定义消息：宏文本注入 (WM_COPYDATA)、退出通知、热键转发
      └─ 去掉嵌入模式下的确认框/错误框/全屏，退出即通知外壳
```

选择双进程的原因：

1. PuTTY 的网络/终端内核成熟稳定，改动量最小化（只加嵌入模式 + 注入 + 主题 + 热键转发）。
2. 每个会话独立进程隔离，一个会话崩溃不影响其他会话和外层 UI。
3. UI 用 C# WinForms 自绘，开发效率高、视觉可控；.NET Framework 4.8 在 Win10/11 内置，**无需额外运行时**，MSI 体积小。
4. 跨进程嵌入技术成熟（SuperPuTTY / MTPuTTY 均采用类似方案）。

## 3. PuTTY 0.85 fork 改动清单（`putty/` 目录）

> 基线：PuTTY 0.85 官方源码（2026-08-16）。GitHub 上的 `github/putty` 镜像停在 0.74，故以官方 0.85 tarball 为基线，导入本仓库 git。

新增命令行参数（`windows/cmdline.c`、`windows/main.c`/`windows/window.c` 相关）：

| 参数 | 说明 |
| --- | --- |
| `-xputty-embed <hwnd>` | 以 WS_CHILD 方式创建到指定父窗口（十进制或 0x 十六进制） |
| `-xputty-host <hwnd>` | 外壳窗口句柄：用于退出/热键/错误通知 |
| `-xputty-theme <path>` | 主题 INI：字体、字号、前/背景色、ANSI16 色、光标、回滚行数 |
| `-xputty-title <text>` | 覆盖窗口标题（外壳用来标记） |

嵌入模式行为：
- 窗口创建为 `WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN`，去掉标题栏/边框/系统菜单/最大化按钮；不响应 Alt+Enter 全屏。
- 关闭确认（ConfirmOnClose）、错误 MessageBox 一律不弹，改为 `WM_APP` 通知外壳后退出。
- 会话结束：通知外壳（含退出码/错误文本）后进程退出，由外壳决定是否保留窗格显示「已断开」。
- 热键转发：`Ctrl+T/Ctrl+W/Ctrl+Tab/Ctrl+Shift+Tab/Ctrl+Shift+W/F11/Ctrl+Shift+M` 等转发给外壳，不输入终端。
- 粘贴/宏注入：注册消息 `xputty-input`（`RegisterWindowMessage`），外壳用 `WM_COPYDATA` 携带 UTF-8 文本 + flags（auto-enter）注入终端（走 bracketed-paste 逻辑，多行不弹确认）。
- PuTTY 自带的右键菜单、复制粘贴、滚轮、颜色主题功能保留。

主题 INI 格式（示例）：

```ini
[terminal]
font=Cascadia Mono
font-size=11
scrollback=5000
cursor=block
[colors]
foreground=#E6EAF2
background=#0F1115
cursor=#4C8DFF
ansi0=#161A22
...
ansi15=#F2F4F8
```

## 4. WinForms 外壳（`shell/` 目录）

技术：C# 7.3+（Roslyn csc 编译，target .NET Framework 4.8）、WinForms、自绘控件（GDI+）、DWM 圆角与深色标题栏。

### 4.1 模块划分（文件即边界，便于并行开发）

| 文件 | 职责 |
| --- | --- |
| `App/Program.cs` | 入口、单实例、全局异常处理、DPI 感知 |
| `App/AppContext.cs` | 全局状态：设置、主题、会话库、宏库、实例管理 |
| `App/Theme.cs` | 设计 token（颜色/圆角/间距/字体）、绘制工具（圆角矩形、阴影、图标） |
| `Storage/SessionStore.cs` | 会话 JSON 读写 + 从 PuTTY 注册表导入 |
| `Storage/MacroStore.cs` | 宏 JSON 读写（增删改排序） |
| `Storage/SettingsStore.cs` | 设置 JSON 读写 |
| `Core/PuttyProcess.cs` | 启动 putty.exe、等待 HWND、嵌入、注入宏、监听退出、热键回调 |
| `Core/SplitTree.cs` | 分屏树模型：叶子=Pane，节点=水平/垂直分割，序列化恢复 |
| `UI/MainForm.cs` | 无边框主窗、自绘标题栏、全局布局、命令分发 |
| `UI/TabBar.cs` | 标签栏：新建/关闭/拖动排序/中键关闭/活动态 |
| `UI/SplitHost.cs` | 分屏容器控件：递归渲染 + 拖拽分隔条 + 布局预设 |
| `UI/PaneView.cs` | 单个窗格：嵌入 putty 子窗口、焦点、断开遮罩、最大化/关闭 |
| `UI/SessionSidebar.cs` | 会话侧栏：搜索、分组树、双击打开、右键菜单、导入 PuTTY 会话 |
| `UI/MacroBar.cs` | 宏工具条：横向滚动按钮、hover 态、点击发送 |
| `UI/StatusBar.cs` | 状态栏：连接状态、编码、延迟、行列/串口参数 |
| `UI/Dialogs/MacroEditorForm.cs` | 宏管理弹窗（列表+表单+快捷键录制+颜色选择） |
| `UI/Dialogs/SessionEditorForm.cs` | 新建/编辑会话（SSH / Telnet / Serial / Raw） |
| `UI/Dialogs/SettingsForm.cs` | 设置（主题/字体/宏条位置/关闭行为） |
| `UI/Controls/*.cs` | 通用自绘控件：FlatButton、IconButton、TextBox、Toggle、Dropdown、List |

### 4.2 关键交互

- **标签页**：`Ctrl+T` 新建、`Ctrl+W` 关闭当前、`Ctrl+Tab / Ctrl+Shift+Tab` 切换、中键关闭、拖动排序。
- **分屏**：标签栏右侧分屏按钮；`Ctrl+Shift+D` 纵向分屏、`Ctrl+Shift+E` 横向分屏；分隔条可拖拽；每窗格右上角 hover 出现「最大化 / 关闭」；布局预设菜单（2x2、1x3…）；关闭最后一个窗格即关闭标签。
- **宏**：工具条显示所有启用宏（超出横向滚动）；点击发送到当前焦点窗格；`Ctrl+Shift+1..9` 触发前 9 个；「+ 配置宏」打开宏管理弹窗。
- **会话**：双击侧栏会话在新标签打开；「新建会话」按钮弹会话编辑；右键菜单（打开/编辑/复制/删除/打开到分屏）；搜索过滤；从 PuTTY 导入。
- **主题**：默认深色，可在设置中切换浅色；主题同时决定 PuTTY 终端配色（生成 theme.ini 传给 putty.exe）。

### 4.3 数据与路径

- 安装目录：`%ProgramFiles%\xPuTTY\`（xputty.exe、putty.exe、plink.exe、puttygen.exe、themes 目录）
- 用户数据：`%APPDATA%\xPuTTY\sessions.json`、`macros.json`、`settings.json`、`logs\`
- 会话凭据：仅保存用户名/密钥路径，不保存明文密码（可选保存密码需 DPAPI 加密，v1 不实现）

## 5. 构建与打包

```
build.ps1
 ├─ [1] PuTTY fork:  vcvars64 + cmake -G Ninja + ninja    → build\putty\putty.exe
 ├─ [2] Shell:       Roslyn csc /target:winexe /r:.NET4.8  → build\shell\xputty.exe
 └─ [3] MSI:         WiX candle + light                    → dist\xPuTTY-1.0.1-x64.msi
```

- 编译器：VS2022 BuildTools（MSVC 14.44、Windows SDK 10.0.26100、自带 cmake/ninja、Roslyn csc），无需额外安装 .NET SDK。
- MSI：WiX Toolset 3.14.1（`tools/wix/`），per-machine 安装、开始菜单快捷方式、可选桌面快捷方式、自动升级 GUID、卸载干净。
- 版本号：`1.0.1`，卸载显示名「xPuTTY」，发布者自定。

## 6. 测试要点

1. putty.exe 基线：SSH 连接、串口连接、复制粘贴、缩放窗口。
2. 嵌入：新建标签/分屏后终端尺寸正确、焦点正确、键盘输入正常、鼠标滚轮正常。
3. 宏注入：ASCII/中文/多行文本、自动回车、焦点跟随、宏编辑弹窗增删改排序持久化。
4. 分屏：2 / 4 / 5 个窗格、拖拽分隔条、窗格最大化、关闭窗格、标签拖动排序。
5. 会话：新建 SSH/串口会话、保存、重启后恢复、导入 PuTTY 会话。
6. MSI：全新安装、覆盖升级、卸载后无残留（用户数据保留）、开始菜单启动正常。
