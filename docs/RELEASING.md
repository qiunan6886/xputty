# 版本管理与发布流程

仓库：`https://github.com/qiunan6886/xputty`（当前为 **private**，可在 Settings → Danger Zone 改为 public）

## 分支与标签

- 主分支：`main`
- 标签（annotated）：`v1.0.0`、`v1.0.1`、`v1.0.2`
- 每个 GitHub Release 附 MSI 安装包、`SHA256SUMS.txt`、`BUILD-INFO.txt`

## 发新版本步骤

1. 修改版本号（两处需一致）：
   - `shell/src/UI/MainForm.cs` → `AppVersion`（标题栏显示）
   - `shell/src/UI/Dialogs/SettingsForm.cs` → 关于文本
2. 更新 `CHANGELOG.md` 与 `docs/RELEASE.md`。
3. 构建并验证（约 1~2 分钟，需要 VS2022 BuildTools）：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build-all.ps1 -Fresh -Verify -Version 1.0.3
   ```

   产物：`dist\xPuTTY-1.0.3-x64.msi`；`dist\SHA256SUMS.txt`、`dist\BUILD-INFO.txt` 同步更新。
4. 提交并打标签（annotated tag，便于 `git describe`）：

   ```powershell
   git add -A
   git commit -m "release: v1.0.3"
   git tag -a v1.0.3 -m "xPuTTY v1.0.3 - <一句话说明>"
   git push origin main
   git push origin v1.0.3
   ```
5. 在 GitHub 创建 Release 并上传 `dist` 下的 MSI + 两个清单文件（也可以：

   ```powershell
   # 需要 GitHub token（repo 权限）
   # POST /repos/qiunan6886/xputty/releases -> 得到 release id
   # POST https://uploads.github.com/repos/qiunan6886/xputty/releases/<id>/assets?name=<file>
   ```

## 目录约定

| 路径 | 是否入库 | 说明 |
| --- | --- | --- |
| `putty/`、`shell/`、`installer/`、`design/`、`docs/` | ✅ | 源码、设计、文档 |
| `build/`、`dist/`、`_dl/`、`_scratch/`、`tools/wix/` | ❌（.gitignore） | 构建产物、下载缓存、WiX 二进制 |
| MSI 安装包 | 作为 Release 资产 | 不入 git，避免仓库膨胀 |

## 环境依赖（重新构建）

- Windows 10/11 x64
- Visual Studio 2022 BuildTools（MSVC 14.44+、Windows SDK 10.0.26100+、自带 CMake/Ninja/Roslyn csc）
- WiX 3.14.1（`tools/wix`，可从 `wixtoolset/wix3` releases 获取）
- PuTTY 源码已包含在 `putty/`，无需额外下载
