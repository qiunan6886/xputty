# xPuTTY 1.0.1 安装包（WiX 3.14.1 / MSI）

本目录包含 xPuTTY 最终安装包的定义与构建/验证脚本。

| 文件 | 说明 |
|---|---|
| `product.wxs` | 最终 MSI 定义（x64、per-machine、ProgramFiles64Folder\xPuTTY） |
| `license.rtf` | 安装向导“许可协议”页显示的 PuTTY LICENCE（MIT） |
| `assets\themes\default.ini` | 安装包内置的默认终端主题（`themes\default.ini`） |
| `verify-msi.ps1` | MSI 验证脚本：`msiexec /a` 解包哈希比对 + per-user 安装/卸载自测 |
| `README.md` | 本文件 |

一键构建入口在项目根目录：`D:\workspace\xputty\build-all.ps1`。

---

## 1. 构建

```powershell
# 完整流水线（PuTTY fork -> 外壳 -> stage -> MSI -> SHA256 清单）
powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1

# 清空重建（删除 build\putty-fork 与 WiX 中间目录）
powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1 -Fresh

# 只重建 MSI（复用已构建的 fork/外壳产物）
powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1 -SkipPutty -SkipShell

# 构建 + 自动验证（推荐）
powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1 -Verify
```

流水线步骤：`[1] PuTTY fork`（`putty,plink,pscp,psftp,puttygen,pageant`）→
`[2] shell\build-shell.ps1` → `[3] build\stage` 组装 → `[4] candle+light` →
`[5] SHA256 清单` → `[6] 可选 -Verify`。

产物：

```
D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi     # 最终安装包
D:\workspace\xputty\dist\SHA256SUMS.txt           # stage 文件 + MSI 的 SHA256
D:\workspace\xputty\dist\BUILD-INFO.txt           # 版本/提交/哈希/耗时
D:\workspace\xputty\build\stage\                  # MSI 的输入 staging 目录
```

### 占位外壳策略（重要）

最终 MSI 必须包含真实的 WinForms 外壳 `build\shell\xputty.exe`。
`build-all.ps1` 会检查：

1. 外壳可执行文件是否存在；
2. 是否**比 `shell\src` 源码旧**（旧了说明没重新编译）；
3. 是否与 `putty.exe` **字节完全相同**（说明是占位文件）。

任一条件命中时，默认**直接报错退出**（exit 1），不会生成可发布的 MSI。
只有在显式传 `-AllowPlaceholder` 时才继续，并且：

- MSI 命名为 `xPuTTY-1.0.1-x64-PLACEHOLDER.msi`；
- `dist\PLACEHOLDER-BUILD-WARNING.txt` 写明“不可发布”；
- 控制台输出 `[PLACEHOLDER - NOT FOR RELEASE]`。

外壳就绪后重新执行（不带 `-AllowPlaceholder`），即生成正常的
`dist\xPuTTY-1.0.1-x64.msi`。

---

## 2. 安装

最终包为 **per-machine**（安装到 `C:\Program Files\xPuTTY`），需要管理员权限。

```
双击 dist\xPuTTY-1.0.1-x64.msi      # 图形向导（WixUI_InstallDir，可改安装目录）
msiexec /i "D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi" /qn    # 静默（管理员命令行）
```

安装内容：

```
C:\Program Files\xPuTTY\
├─ xputty.exe            xPuTTY 外壳（主程序）
├─ putty.exe             改造版 PuTTY 0.85 终端内核
├─ plink.exe pscp.exe psftp.exe puttygen.exe pageant.exe
├─ LICENCE.txt           PuTTY LICENCE（MIT）
├─ README-PuTTY.txt      PuTTY 说明
└─ themes\default.ini    默认终端主题
开始菜单：xPuTTY 文件夹 → xPuTTY / PuTTY / PuTTYgen
桌面：xPuTTY 快捷方式（默认创建）
```

产品信息：`ProductName=xPuTTY`、`Manufacturer=xPuTTY`、`Version=1.0.1`、
固定 `UpgradeCode={7F3B9C1A-2E84-4D5B-9A67-C0B1E5D4F2A3}`、`ARPPRODUCTICON` 使用
`shell\assets\xputty.ico`（9 帧 16–256px 32bpp ICO）。

安装条件：`VersionNT64` 且系统构建号 ≥ 10240（Windows 10/11 x64）。
> 注：本机 Windows Installer 报告的 `VersionNT`/`WindowsBuild` 是 603/9600（Win7/8.1 时代的兼容值），
> 因此条件改用注册表 `CurrentBuildNumber`，避免在 Windows 11 上被误拦截。

不写系统 PATH、不注册文件关联。

## 3. 卸载

```
设置 → 应用 → 已安装的应用 → xPuTTY → 卸载
msiexec /x "D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi" /qn     # 静默
```

卸载会删除安装目录、开始菜单文件夹、桌面快捷方式和安装器注册的 HKCU/HKLM 组件键。
**用户数据保留**（外壳保存的会话/宏/设置位于用户配置目录，不属于 MSI 组件）。

## 4. 升级

- 固定 `UpgradeCode` + `MajorUpgrade`：直接安装更高版本 MSI 即可覆盖升级（旧版本被卸载、用户数据保留）。
- 允许同版本重装（`AllowSameVersionUpgrades="yes"`，便于开发迭代）。
- 降级会被阻止并提示 “A newer version of xPuTTY is already installed.”。

## 5. 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\installer\verify-msi.ps1 `
    -MsiPath D:\workspace\xputty\dist\xPuTTY-1.0.1-x64.msi `
    -StageDir D:\workspace\xputty\build\stage `
    -InstallerDir D:\workspace\xputty\installer
```

包含三部分：

| 验证 | 内容 | 结果（本机实测） |
|---|---|---|
| A. `msiexec /a` 管理安装 | 解包 MSI，逐个比对 stage 文件 SHA256 | 全部一致（含 `themes\default.ini`） |
| B. per-user 生命周期自测 | 用同一 `product.wxs` 加 `-dScope=perUser` 生成测试 MSI，静默安装→校验文件/开始菜单/桌面快捷方式/哈希→静默卸载→确认目录/快捷方式/注册表清空 | 全部 PASS |
| C. 真实 per-machine 安装 | 需要管理员；非提权会话记录 `Error 1925`（预期） | 见下方限制 |

安装包本体（per-machine）的静默安装请用管理员命令行执行：
`msiexec /i dist\xPuTTY-1.0.1-x64.msi /qn`。

## 6. 已知限制

1. **未签名**：MSI 及 exe 未做代码签名，Windows SmartScreen / 部分杀软会提示
   “Windows 已保护你的电脑”。请点“更多信息 → 仍要运行”，或为构建产物配置企业代码签名证书。
2. **per-machine 安装需要管理员权限**；普通用户会话静默安装会返回 Error 1925（预期）。
3. **仅 x64 Windows 10/11**（`Platform="x64"`，无 32 位包；无 ARM64 包）。
4. 不写 PATH、不注册文件关联（按产品要求）。
5. 外壳仍是开发中产物时，请不要分发 `*-PLACEHOLDER.msi`。
6. 安装包版本固定为 `1.0.1`；正式发版前按需修改 `-Version` 与 `product.wxs` 的 ProductVersion。
7. 内置主题仅一份 `themes\default.ini`；运行期主题由外壳生成到用户数据目录，升级不会覆盖用户主题。
