# 安装器事件诊断：2026-07-14

## 结论

用户从 Codex 中打开本地 `SwSearch-Setup-x64.exe` 链接后，Codex Desktop 发生了约 6 秒的客户端重启。现有证据不能证明 SwSearch bootstrapper 或 Windows Installer 曾经启动，更没有它们崩溃的证据。最符合证据的解释是 Codex Desktop 在处理本地可执行文件链接时自身退出/重启。

尽管旧安装器未被证实为崩溃源，版本 1.0.0 的 WiX Burn EXE 已作为预防措施永久退役并隔离。版本 1.0.1 只交付标准 per-user x64 MSI，不再包含 Burn/WixInternalUIBootstrapperApplication，并显式禁用 Restart Manager 和重启请求。

## 时间线与证据

- 10:11:45.666：旧 Codex Desktop 日志 `codex-desktop-d481...-60732-...log` 停止写入，末尾没有正常 shutdown、fatal、panic 或未处理异常记录。
- 10:11:51：新 Codex Desktop PID 22172 的三份日志创建；Sentry 新会话开始时间为 10:11:51.217，状态 `ok`、`errors=0`。
- 10:00 之后 Windows Application 日志计数：`Application Error=0`、`Windows Error Reporting=0`、`MsiInstaller=0`、`RestartManager=0`。
- `%TEMP%` 中没有 `SOLIDWORKS 文件检索`、`SwSearch`、WiX Burn 对应日志。
- WER ReportArchive/ReportQueue 没有 Codex、SwSearch、Burn 或 msiexec 报告。
- 因没有 Codex/Application Error/WER 崩溃事件，无法提供 Codex 的故障模块或异常码；系统中唯一的近期 WER 是 08:26 的无关 `iCloudChrome.exe` / `ntdll.dll` / `0xc0000409`。

因此不能把无关的 iCloud 事件归因于本次故障，也不能声称 Setup、Burn 或 msiexec 崩溃。

## 旧包静态审计

- Burn manifest：WiX 5.0.2、x64、`PerMachine=no`、内嵌单一 MSI，BA 为 `WixInternalUIBootstrapperApplication_X64`。
- Bundle/Package/构建脚本中没有 `taskkill`、`Stop-Process`、`TerminateProcess`、`Process.Kill`、Codex 进程检测或产品 CustomAction。
- MSI 只有 WiX UI 库的 `WixUIPrintEula_X64` CustomAction。
- 旧 WixUI 的 FilesInUse 对话框包含 `WixUIRMOption=UseRM` 和 `RMShutdownAndRestart` 标准路径；没有证据表明此路径运行，但替代包显式禁用 Restart Manager。
- `MetadataPipeClient` 中存在的 `process.Kill(entireProcessTree: true)` 仅作用于客户端自己创建并持有句柄的 Metadata Worker，不属于安装器，也无法定位或结束 Codex。

## 隔离

旧 EXE、旧 MSI、旧验收记录和静态反编译/Burn manifest 保存在：

```text
artifacts\quarantine\
```

`BROKEN-DO-NOT-RUN.txt` 明确禁止运行或重新分发旧 EXE。取证文件没有删除。

## 替代方案

- 主安装文件：`artifacts\SwSearch-x64-v1.0.1.msi`
- 标准 Windows Installer UI，不使用 bootstrapper 或嵌套 MSI UI。
- `Scope=perUser`，安装到 `%LOCALAPPDATA%\Programs\SwSearch`。
- `MSIRESTARTMANAGERCONTROL=Disable`，`REBOOT=ReallySuppress`。
- 仍提供主功能、开始菜单快捷方式和默认不选的右键菜单/桌面快捷方式 Feature。
- 未运行 MSI；只通过 OLE 头、WiX 完整反编译、内嵌 CAB 导出、表结构和哈希进行静态验收。

为规避 Codex 本地二进制链接处理问题，不应在 Codex 中点击 MSI/EXE 链接。请在文件资源管理器中手动定位并双击 MSI。
