# xPuTTY 测试与验收记录

测试环境：Windows 11 Pro 26300（x64）、200% DPI、屏幕 3200x2000 物理（1600x1000 逻辑）、VS2022 BuildTools / MSVC 14.44、PuTTY 0.85 基线。
测试日期：2026-11-24。

## 1. 构建验证

| 项 | 结果 |
| --- | --- |
| PuTTY fork（MSVC 14.44 + CMake/Ninja） | 通过，putty.exe x64，无改动时可增量构建 |
| 外壳（Roslyn csc，.NET Framework 4.8） | 通过，26 个源文件，0 error |
| WiX MSI（candle + light） | 通过，per-machine x64 |
| msiexec /a 管理安装解包 | 通过，payload SHA256 与构建产物一致 |
| per-user 安装/卸载全流程（补充验证） | 38/38 PASS，快捷方式与 HKCU 键清理干净 |
| per-machine 真实安装 | 运行环境无管理员权限，Error 1925（预期；终端用户安装时会 UAC 提权） |

## 2. 嵌入与终端

| 用例 | 结果 | 说明 |
| --- | --- | --- |
| putty.exe 以 `-xputty-embed` 作为子窗口嵌入 Pane | ✅ | 子窗口 WS_CHILD、无标题栏、自动填满 Pane |
| 主题 INI 生效（字体/前景/背景/ANSI16/光标） | ✅ | 终端深色配色与外壳一致，光标色可配 |
| 键盘输入 | ✅ | 点击终端后正常输入、回显 |
| 鼠标滚轮 / 复制粘贴（PuTTY 原生） | ✅ | 未改动内核路径 |
| 窗口/Pane 缩放 | ✅ | 拖拽分隔条、最大化窗口后终端网格随动 |
| 宏注入（ASCII/中文/多行/自动回车） | ✅ | WM_COPYDATA 注入，echo 服务端可见；支持 $HOST/$USER 变量 |
| 断开检测与遮罩 | ✅ | 杀服务端后 Pane 显示「连接已断开（退出码 1）」+ 重连/关闭，无系统 MessageBox |
| 重连 | ✅ | 点「重连」后经 IDM_RESTART 重建会话并恢复 |
| 关闭窗格/退出 | ✅ | WM_CLOSE → 进程退出，无 putty 僵尸进程 |

## 3. 外壳功能

| 用例 | 结果 |
| --- | --- |
| 无边框窗口：拖动 / 双击最大化 / Win11 Snap | ✅ |
| 标签：新建（Ctrl+Shift+T）、关闭、Ctrl+Tab 切换 | ✅ |
| **关闭标签页回归（v1.0.1 修复）**：单标签/多标签/分屏内关闭窗格/关闭最后一个标签/中键关闭 | ✅ | 修复前抛 `ObjectDisposedException`（SplitHost.Relayout 对已释放 PaneView 调用 SetVisible）；修复后无异常日志、其余标签会话不受影响、无残留 putty 进程 |
| 分屏：Ctrl+Shift+E 左右、Ctrl+Shift+D 上下、2x2 四窗格 | ✅ |
| 分屏拖拽分隔条、F11 窗格最大化 | ✅ |
| 宏工具条：8 个默认宏、横向滚动、点击发送 | ✅ |
| 宏管理弹窗：增删改、拖拽排序、快捷键、颜色、自动回车、启用 | ✅ |
| 会话侧栏：分组、搜索、双击打开、右键编辑/删除 | ✅ |
| 会话编辑弹窗（SSH/Telnet/串口/Raw、命令行预览） | ✅ |
| 从 PuTTY 注册表导入会话 | ✅ |
| 设置弹窗（字体/字号/侧栏/宏条/关闭行为） | ✅ |
| 200% DPI 布局 | ✅ | 设计像素 × 2 缩放，字号/间距一致 |
| 窗口几何保存/恢复 | ✅ |

## 4. 最终安装包验证

| 项 | 结果 |
| --- | --- |
| 构建 | `build-all.ps1 -Fresh -Verify` → exit 0，70.9 s |
| 产物 | `dist\xPuTTY-1.0.1-x64.msi`，2,805,760 B |
| SHA256 | `28DAF8AD74AC2EECDF7C0DFAB291137933E0B61865ECA748B1020063022CF589` |
| MSI 内容 | xputty.exe(571,392) / putty.exe(1,082,880) / plink / pscp / psftp / puttygen / pageant / LICENCE / README-PuTTY / themes\default.ini / xputty.ico |
| `/a` 管理安装解包 | exit 0；11 个 payload SHA256 与 stage 清单全部一致 |
| per-user 安装/卸载自测 | 38/38 PASS（文件、开始菜单 3 快捷方式、桌面快捷方式、卸载清理） |
| per-machine 真实安装 | 当前测试会话无管理员权限 → Error 1925（预期；终端用户 UAC 提权安装） |
| **安装包冒烟**（解包目录直接运行） | ✅ 界面正常；`--raw` 会话连接成功（状态栏「已连接 · RAW」）；Ctrl+Shift+1 宏注入后终端回显内容；退出后无 putty 残留进程 |
| fork 源码指纹 | window.c `4134E31A…`、xputty.c `6EF8B963…`、xputty.h `2C1F94C7…`、putty.c `74D40FB3…`、windows\CMakeLists.txt `F74F1DD4…` |

## 5. 已知限制

1. 安装包与可执行文件未做代码签名，SmartScreen 会提示未知发布者。
2. 暂不支持浅色主题（规划 v1.1）。
3. PuTTY 原生对话框（主机密钥确认、私钥口令、更改设置）仍为系统风格，未深色化；出现在外壳窗口之上。
4. 未内置 SFTP 面板（可用随包安装的 pscp/psftp 命令行）；未做会话密码保存（只保存用户名/私钥路径，符合用户要求）。
5. 仅 x64 Windows 10/11；不支持 ARM64。
