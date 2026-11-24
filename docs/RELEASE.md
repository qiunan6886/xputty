# xPuTTY v1.0.1 — 交付说明（Release Notes）

## 版本历史

- **v1.0.1（2026-11-24）**：修复关闭标签页时的 `ObjectDisposedException` 崩溃（SplitHost 对已释放 PaneView 重排 / 关闭顺序不当）。关闭单个标签、关闭分屏窗格、关闭最后一个标签、关闭主窗口均已回归验证，无异常、无残留 putty 进程。MSI 版本号同步升至 1.0.1。
- v1.0.0（2026-11-24）：首个完整版本。

## 交付物

| 文件 | 说明 |
| --- | --- |
| `dist/xPuTTY-1.0.1-x64.msi` | Windows x64 安装包（per-machine，Program Files） |
| `dist/SHA256SUMS.txt` | stage 全文件 + MSI 的 SHA256 清单 |
| `dist/BUILD-INFO.txt` | 构建信息：源码指纹、工具链版本、耗时、版本号 |
| `README.md` | 项目说明 / 功能 / 构建 / 安装 |
| `docs/screenshots/` | 实机运行截图 |
| `docs/TESTING.md` | 测试用例与结果 |
| `docs/PUTTY_FORK.md` | PuTTY fork 改动说明 |
| `ARCHITECTURE.md` / `SHELL_SPEC.md` | 架构与外壳开发规范 |
| `design/` | UI 设计稿（PNG/HTML）、设计 token、参考分析 |

MSI：**2,805,760 字节**
SHA256：**`28DAF8AD74AC2EECDF7C0DFAB291137933E0B61865ECA748B1020063022CF589`**
构建：`build-all.ps1 -Fresh -Verify`（exit 0，70.9s，验证 38/38 PASS）

## 安装 / 卸载

```powershell
# 安装（需要管理员；也可双击 MSI）
msiexec /i "D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi"

# 静默安装
msiexec /i "D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi" /qn

# 卸载
msiexec /x "D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi" /qn
```

- 安装位置：`%ProgramFiles%\xPuTTY\`（xputty.exe、putty.exe、plink/pscp/psftp/puttygen/pageant、themes、许可文件）
- 快捷方式：开始菜单 `xPuTTY`（xPuTTY / PuTTY / PuTTYgen）+ 桌面 xPuTTY
- MSI 未签名，SmartScreen 提示时选「更多信息 → 仍要运行」
- 用户数据在 `%APPDATA%\xPuTTY`（sessions.json / macros.json / settings.json / themes / logs），卸载保留

## 功能清单

- 多标签（新建/关闭/拖动排序/Ctrl+Tab），支持会话多开
- 任意树形分屏：左右/上下切分、2x2、1x3、拖拽分隔条、F11 单窗格最大化（突破 MobaXterm「横向只能 2 开」）
- 宏系统：**任意数量**宏按钮（横向滚动，默认 8 个），宏管理弹窗中增删改、拖拽排序、快捷键、颜色标签、自动回车、启用/停用、`$HOST/$USER/$PORT` 变量
- 会话管理：分组/搜索/新建（SSH / Telnet / 串口 / Raw）/编辑/删除、从 PuTTY 导入现有会话、串口扫描、密钥管理入口
- 终端：完整 PuTTY 0.85 内核（SSH 密钥、Pageant、串口、复制粘贴、滚轮、256 色、窗口自适应），深色主题与外壳一致
- 断开检测：Pane 内显示错误原文遮罩 + 一键重连，无系统级报错弹窗
- 无边框自绘界面、Win11 圆角/深色/贴靠布局、200% DPI 支持
- .NET Framework 4.8（Win10/11 内置），无额外运行时依赖

## 验证摘要

- PuTTY fork 与外壳构建 0 error；MSI `/a` 解包 11 个 payload 哈希一致；per-user 安装/卸载 38/38 PASS
- **安装包冒烟**：从 MSI 解包目录启动 xputty.exe → 界面正常、Raw 会话连接成功（状态栏「已连接」）、宏注入生效（终端回显）
- 实机（Windows 11 / 200% DPI）验证：单会话、2/3/4 窗格、宏注入、断开遮罩、重连、F11、关闭无僵尸进程

## 已知限制（v1.1 计划）

1. 未代码签名（SmartScreen 提示）
2. 浅色主题未启用（v1.1）
3. 断开通知未携带 pane HWND，极端并发断开时归属靠启发式（v1.1 改为带 HWND）
4. 布局持久化（分屏结构）未做，窗口几何已持久化
5. PuTTY 原生对话框（主机密钥确认、私钥口令）仍为系统风格
6. 仅 x64 Windows 10/11

## 重新构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build-all.ps1 -Fresh -Verify
```

依赖：Windows 10/11 x64、VS2022 BuildTools（MSVC/SDK/CMake/Ninja/Roslyn csc）、项目内 `tools/wix`（WiX 3.14.1）。
