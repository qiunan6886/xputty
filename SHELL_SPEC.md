# xPuTTY 外壳（C# WinForms）开发规范

## 0. 目标与边界

`xputty.exe` 是 xPuTTY 的 UI 外壳，负责：会话管理、多标签、任意分屏、宏工具条与宏管理弹窗、主题、状态栏。终端本身由改造后的 `putty.exe` 子进程嵌入到外壳的 Pane 面板中。

- 目标框架：.NET Framework 4.8（Win10/11 自带，无需安装运行时）
- 编译器：Roslyn `csc.exe`（VS2022 BuildTools 自带），**不要**用 dotnet SDK，不引入 NuGet 包
- UI：WinForms + 全自绘（GDI+），深色主题；参照 `design/screenshots/*.png` 与 `design/DESIGN.md`
- 中文界面
- 代码可直接使用 C# 7.3 语法（Roslyn 默认支持更高，但不要用 records/init 等需要新运行时的特性）

## 1. 目录与文件归属（并行开发边界）

```
shell/
  src/
    App/Program.cs                # 入口、单实例、DPI、异常兜底        [dev-core]
    App/AppContext.cs             # 全局上下文：设置/会话库/宏库/动作   [dev-core]
    App/Theme.cs                  # 设计 token + GDI+ 绘制工具          [dev-core]
    Core/PuttyMessages.cs         # 注册消息 + WM_COPYDATA 注入 P/Invoke [dev-core]
    Core/PuttyProcess.cs          # 启动/嵌入/关闭/重连/通知处理        [dev-core]
    Core/SplitTree.cs             # 分屏树模型（序列化/遍历/增删）      [dev-core]
    Storage/Json.cs               # DataContractJsonSerializer 封装     [dev-core]
    Storage/SessionStore.cs       # 会话持久化 + PuTTY 会话导入         [dev-core]
    Storage/MacroStore.cs         # 宏持久化                            [dev-core]
    Storage/SettingsStore.cs      # 设置持久化                          [dev-core]
    UI/MainForm.cs                # 无边框主窗、标题栏、整体布局、命令分发 [dev-core]
    UI/TabBar.cs                  # 标签栏                              [dev-core]
    UI/SplitHost.cs               # 分屏容器（递归渲染、拖拽分隔条）    [dev-core]
    UI/PaneView.cs                # 单窗格（嵌入 putty、焦点、断开遮罩）[dev-core]
    UI/StatusBar.cs               # 状态栏                              [dev-core]
    UI/SessionSidebar.cs          # 会话侧栏                            [dev-panels]
    UI/MacroBar.cs                # 宏工具条                            [dev-panels]
    UI/Controls/ChromeButton.cs   # 通用自绘按钮/图标按钮               [dev-panels]
    UI/Controls/FlatInput.cs      # 自绘输入框（可包 TextBox）          [dev-panels]
    UI/Controls/FlatToggle.cs     # 开关                                [dev-panels]
    UI/Controls/FlatDropdown.cs   # 下拉选择                            [dev-panels]
    UI/Controls/ScrollList.cs     # 通用滚动列表                        [dev-panels]
    UI/Dialogs/ModalForm.cs       # 弹窗基类（遮罩、圆角、标题栏）      [dev-panels]
    UI/Dialogs/MacroEditorForm.cs # 宏管理弹窗                          [dev-panels]
    UI/Dialogs/SessionEditorForm.cs # 新建/编辑会话                     [dev-panels]
    UI/Dialogs/SettingsForm.cs    # 设置弹窗                            [dev-panels]
  build-shell.ps1                 # 编译脚本                           [lead]
  app.manifest                    # DPI 感知                           [lead]
```
**规则：只改自己归属的文件**。需要改对方文件/公共接口时，先 send_message 给 lead。编译脚本 `build-shell.ps1` 会递归编译 `shell/src/**/*.cs`，不需要维护文件列表。

## 2. 数据模型（放在 `App/AppContext.cs`）

```csharp
public enum ProtocolKind { Ssh, Telnet, Serial, Raw }

public class SessionDef {
    public string Id;            // Guid 字符串
    public string Name;          // 显示名
    public string Group;         // 分组（"生产环境" 等；空=未分组）
    public ProtocolKind Protocol;
    public string Host;          // SSH/Telnet/Raw 主机
    public int Port;             // 默认 22/23/0
    public string User;          // 用户名
    public string KeyFile;       // SSH 私钥路径（可空）
    public string SerialPort;    // "COM3"
    public string SerialConfig;  // "115200,8,n,1,N"
    public string ColorTag;      // 颜色标签（宏/会话共用，"blue"/"green"...）
    public string TerminalTheme; // 主题名，默认 "default"
    public bool Pin;             // 置顶到常用
}

public class MacroDef {
    public string Id;
    public string Name;          // 按钮文字（2-6 字）
    public string Text;          // 发送内容，支持 \r \n 字面量转义、$HOST 变量
    public string Hotkey;        // 例如 "Ctrl+Shift+1"（空=无）
    public string Color;         // 颜色标签
    public bool SendEnter;       // 追加回车
    public bool Enabled;         // 是否显示在工具条
    public int Sort;             // 排序
    public string Scope;         // "global" 或 "session:<id>"（v1 只用 global）
}

public class AppSettings {
    public string Theme;         // "dark" / "light"
    public string Accent;        // "#4C8DFF"
    public int FontSize;         // 13 (终端字号)
    public string FontName;      // "Cascadia Mono"
    public bool ShowSidebar;
    public bool ShowMacroBar;
    public bool RestoreLastLayout;
    public string CloseBehavior; // "ask" / "exit" / "tray"
}
```

## 3. AppContext（单例）公共 API

```csharp
public sealed class AppContext {
    public static AppContext I { get; }
    public AppSettings Settings;
    public List<SessionDef> Sessions;
    public List<MacroDef> Macros;
    public string DataDir;      // %APPDATA%\xPuTTY
    public string PuttyExe;     // 同目录 putty.exe
    public Theme Theme;         // 当前主题 token
    public MainForm Main;       // 主窗（Program 里赋值）
    public string BuildThemeIni();             // 生成/覆盖 themes\current.ini，返回路径

    public void SaveAll();                     // 持久化设置/会话/宏
    public MacroDef NewMacro();                // 追加并返回
    public void DeleteMacro(string id);
    public void MoveMacro(string id, int newIndex);
    public SessionDef NewSession();
    public void DeleteSession(string id);
    public int ImportPuttySessions();          // 返回导入数量，读 HKCU\Software\SimonTatham\PuTTY\Sessions
    public void OpenSession(SessionDef s, bool newTab = true);
    public void SendMacro(MacroDef m);         // 发给当前焦点窗格
}
```

## 4. 与 putty.exe 的进程协议（必须严格按此实现）

启动命令（参数顺序按此拼接，路径加引号）：

```
putty.exe -xputty-embed <panePanel.Handle> -xputty-host <MainForm.Handle> -xputty-theme "<theme.ini>" <连接参数>
连接参数：
  SSH:    -ssh -P <port> [-l <user>] [-i "<keyfile>"] <host>
  Telnet: -telnet -P <port> <host>
  Raw:    -raw -P <port> <host>
  Serial: -serial <COMx> -sercfg <115200,8,n,1,N>
```
注意：`panePanel.Handle` 是 WinForms 面板的 HWND；putty 会把自己创建为一个无边框子窗口并填满该面板。Windows 缩放（DPI）由两者各自处理，外壳必须声明 per-monitor DPI aware（见 app.manifest）。

窗口消息（`RegisterWindowMessage`，进程间同名即可）：
| 消息 | wParam | lParam |
| --- | --- | --- |
| `xPuTTYCreated` | 终端子窗口 HWND（IntPtr） | 0 |
| `xPuTTYSessionExit` | 退出码 | 0 |
| `xPuTTYHotkey` | 虚拟键码 | Shift 是否按下 (0/1) |
| `xPuTTYFatal` | 0 | 同步 `SendMessage`，`Marshal.PtrToStringAnsi(lParam)` 错误文本 |

- **宏注入**：`SendMessageTimeout(childHwnd, WM_COPYDATA(0x004A), mainForm.Handle, ref COPYDATASTRUCT, SMTO_ABORTIFHUNG, 2000, out _)`；
  `dwData = 0x58505459`，`cbData = 4 + utf8.Length`，`lpData` 前 4 字节小端 flags（bit0=追加回车），随后 UTF-8 文本。**不要**发 `\n`，换行用 `\r`（shell 侧把 `\r\n`/`\n` 转成 `\r`）。
- **关闭窗格**：`PostMessage(childHwnd, WM_CLOSE(0x0010), 0, 0)`。
- **重连**：`PostMessage(childHwnd, WM_COMMAND(0x0111), (IntPtr)0x0040, IntPtr.Zero)`（0x0040 = PuTTY 的 IDM_RESTART）。
- **焦点**：跨进程 SetFocus 需 `AttachThreadInput(GetCurrentThreadId(), GetWindowThreadProcessId(child), true); SetFocus(child); AttachThreadInput(..., false)`。
- **子窗口点击**：PaneView 的 Panel 会收到 `WM_PARENTNOTIFY`（0x0210）→ 激活该 pane 并设置焦点。
- puTTY 转发给外壳的热键（外壳需处理）：`Ctrl+Shift+T` 新标签、`Ctrl+Shift+W` 关闭窗格/标签、`Ctrl+Tab`/`Ctrl+Shift+Tab` 切换标签、`Ctrl+Shift+E` 右分屏、`Ctrl+Shift+D` 下分屏、`Ctrl+Shift+M` 宏管理、`F11` 窗格最大化、`Ctrl+Shift+1..9` 触发对应宏。外壳自身获得焦点时用 `ProcessCmdKey` 处理同一套热键。

## 5. 布局与视觉规格（严格对齐 design/）

- 主窗：无边框（FormBorderStyle=None）+ 自绘标题栏 40px：左侧 logo+`xPuTTY`+版本+当前环境；中部可留搜索；右侧自绘 最小化/最大化/关闭 按钮（46x40，hover 变亮，关闭 hover 红底 #C42B1C）；`WM_NCHITTEST` 返回 HTCAPTION/HTMINBUTTON/HTMAXBUTTON/HTCLOSE。
- 左侧会话侧栏 240px：顶部“+ 新建会话”按钮(34px, 强调色底)、搜索框(32px)、分组树（组行 27px，会话行 45px：协议徽标 34x18、名称 12.5px、host:port 10.5px 次级色、右侧状态点 6px）；底部：串口扫描 / 密钥管理 / 从 PuTTY 导入 入口。
- 顶部标签栏 38px：标签 32px 高、最多 210px、关闭按钮、活动标签 bg3+底部 2px 强调线；右侧分屏按钮组（竖分/横分/布局预设/标签菜单）。
- 终端区：SplitHost 递归渲染分屏树；分隔条 1px+4px 热区，拖动时显示预览；每个窗格 26px 窗格标题（活动/非活动色不同，含会话名+协议+IP+最大化/关闭）；窗格内嵌 putty 子窗口。
- 宏工具条 44px：左侧“宏”标签；宏按钮 28px 高、6px 圆点颜色、hover bg3、点击发送；末尾虚线“+ 配置宏”；超出宽度横向滚动（滚轮+左右按钮）。
- 状态栏 26px：左 连接状态点+会话名；中 `UTF-8 | xterm-256color | <cols>x<rows> | LF`；右 延迟、加密、主题。
- 颜色/字体/圆角/间距全部取 `design/design-tokens.json` 与 `design/DESIGN.md`；Theme.cs 中定义常量 + GDI+ 辅助：`RoundRect`、`DrawText`（TextRenderer）、`DrawIconSvg`（用 GraphicsPath 画简单线性图标即可，不引外部库）。

## 6. 功能验收清单（v1）

1. 双击侧栏会话 → 新标签打开终端；终端可用键盘输入、滚动、复制粘贴（PuTTY 原生）。
2. `+` / Ctrl+Shift+T 新建标签；Ctrl+Tab 切换；中键/× 关闭；标签可拖动排序。
3. 分屏：按钮/热键切分，支持 2、3、4 及以上窗格；拖分隔条；F11 最大化当前窗格；关闭最后窗格关闭标签。
4. 宏：工具条显示所有启用宏（不限于 4 个）；点击发送到焦点窗格；“+ 配置宏”打开宏管理弹窗；增删改、拖拽排序、快捷键、颜色、自动回车、启用；重启程序后保留。
5. 会话：新建 SSH/串口会话并保存；重启后仍在；从 PuTTY 导入现有会话。
6. 主题：默认深色，与 design 稿一致；终端配色通过 theme.ini 生效。
7. 断开：连接断开时窗格显示“已断开”遮罩 + 重连/关闭按钮；重连发 IDM_RESTART。
8. 无边框窗口可拖动/双击最大化/贴靠（Win11 Snap Layouts 通过 WM_NCHITTEST 的 HTMAXBUTTON）。

## 7. 构建

```powershell
powershell -ExecutionPolicy Bypass -File D:\workspace\xputty\shell\build-shell.ps1
```
输出 `D:\workspace\xputty\build\shell\xputty.exe`。每次改完代码必须跑一遍确保 0 error。

## 8. 面板 / 主窗接口契约（两端必须按此实现）

`MainForm` 公开字段与方法（dev-core 实现，dev-panels 调用）：

```csharp
public SessionSidebar Sidebar;   // 由 MainForm 创建并停靠
public MacroBar MacroBar;
public TabBar TabBar;
public SplitHost SplitHost;
public StatusBar StatusBar;

public void NewTab();                                  // 新标签（打开当前选中会话或空白占位）
public void OpenSessionInTab(SessionDef s, bool newTab);
public void CloseActivePane();
public void SplitActive(bool vertical);                // true=左右分, false=上下分
public void NextTab(bool forward);
public void MaximizeActivePane();
public void SendMacroToActivePane(MacroDef m);
public void OpenMacroEditor();                         // 打开宏管理弹窗
public void OpenSessionEditor(SessionDef s);           // null=新建
public void OpenSettings();
public void RefreshSessions();
public void RefreshMacros();
public void ToggleSidebar();
public void HandleShellHotkey(int vk, bool shift);     // puTTY 转发或 ProcessCmdKey 调用
```

面板控件契约（dev-panels 实现）：

```csharp
public class SessionSidebar : Control {
    public event Action<SessionDef> SessionActivated;  // 双击/回车/单击？双击
    public event Action NewSessionRequested;
    public event Action ImportPuttyRequested;
    public event Action<SessionDef> EditSessionRequested;
    public event Action<SessionDef> DeleteSessionRequested;
    public void SetSessions(List<SessionDef> sessions, string filter);
    public void InvalidateList();
}

public class MacroBar : Control {
    public event Action<MacroDef> MacroClicked;
    public event Action ConfigureRequested;            // “+ 配置宏”
    public event Action<MacroDef> MacroContextRequested; // 右键编辑/删除（可选）
    public void SetMacros(List<MacroDef> macros);      // 已按 Sort 排序、只含 Enabled
}

public class TabBar : Control {                        // dev-core
    public event Action<int> TabSelected;
    public event Action<int> TabCloseRequested;
    public event Action NewTabRequested;
    public event Action<int,int> TabMoved;             // from,to
    public event Action SplitRequested;                // true=纵向
    public void SetTabs(List<TabInfo> tabs, int active);
}

public class SplitHost : Control {                     // dev-core
    public SplitTree Tree;
    public PaneView ActivePane;
    public event Action<PaneView> ActivePaneChanged;
    public void Rebuild(SplitTree tree);
}

public class PaneView : Control {                      // dev-core
    public Panel EmbedPanel;                           // putty 子窗口的父 HWND
    public IntPtr PuttyHwnd;
    public SessionDef Session;
    public bool Connected;
    public void SetDisconnected(string reason);
    public void Close();
}
```

弹窗契约：

```csharp
public class MacroEditorForm : Form { public bool Accepted; public MacroEditorForm(List<MacroDef> macros); }
public class SessionEditorForm : Form { public bool Accepted; public SessionEditorForm(SessionDef s); }
public class SettingsForm : Form { public bool Accepted; public SettingsForm(AppSettings s); }
```

弹窗行为：`ShowDialog(MainForm)`；用户点保存后 `Accepted=true` 且把修改写回传入对象（列表/对象的拷贝语义：传入 clone，保存时由调用方合并）。
