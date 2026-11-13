# SwSearch 1.1.0 升级卡住事故与 1.1.1 修正

> 后续状态：1.1.1 因 MSI ProductVersion 与第一方二进制/Indexer CLI 仍为 1.1.0 而撤销。
> 版本一致性修复自 1.1.2 起生效；1.1.3 使用新的 ProductCode 并保持同一 UpgradeCode，1.1.1 不得安装或交付。

日期：2026-07-14

## 结论

`SwSearch-x64-v1.1.0.msi` 已永久撤回，不得安装或交付。它及旧哈希、旧验证报告仅保留在
`artifacts/quarantine/revoked-v1.1.0/` 用于取证。

现场升级时，旧版 `SwSearch.App` 仍在运行并锁定安装目录中的大量文件。1.1.0 同时禁用了
Restart Manager、没有自身进程预检，并把 `RemoveExistingProducts` 放在
`InstallInitialize` 之前，因此 Windows Installer 在“正在验证安装”阶段无法安全推进。
此前验收只完成了静态检查和 administrative extraction，没有完成真实升级路径验证；
该验证范围在当时的交付表述中不够清楚。

## 1.1.1 修正

- 新 ProductCode：`{25A0D431-5981-43D8-A996-8CAB7E77BA84}`。
- UpgradeCode 保持：`{D9158356-29B3-44D6-A15A-ACDCE8328703}`。
- WiX Util `CloseApplication` 只匹配四个产品自身的唯一可执行文件名：
  `SwSearch.App.exe`、`SwSearch.PropertyViewer.exe`、
  `SwSearch.Metadata.Worker.exe`、`SwSearch.Indexer.exe`。
- 只发送 `WM_CLOSE` 并等待；不设置 `TerminateProcess`、EndSession、
  ElevatedCloseMessage 或 PromptToContinue，不强制结束任何进程。
- 仍存活的自身进程通过四个大写属性汇总，由 MSI Type 19 在任何系统修改之前明确失败。
- 实际 MSI 表序列必须满足：
  `CloseApplications < Type19 < InstallValidate < InstallInitialize < RemoveExistingProducts < ProcessComponents`。
- `RemoveExistingProducts` 调整为 `afterInstallInitialize`，旧版移除纳入事务；后续安装失败时
  Windows Installer 可回滚并恢复旧版。
- Restart Manager 继续禁用，重启请求继续设为 `ReallySuppress`。
- MSI 包内默认开启 `voicewarmupx!` 详细日志，错误消息给出
  `[MsiLogFileLocation]` 与 `%TEMP%\MSI*.LOG` 指引。

## 验证边界

构建验收包括 Release clean/restore/build/test、WiX 标准 ICE（ICE38 保持启用）、完整反编译、
CAB 导出、只读 MSI 数据库序列与 CloseApplication 表断言，以及不注册产品的
administrative extraction。真实首次安装、升级、取消回滚、卸载和残留进程阻断测试必须在
隔离测试账户或虚拟机执行；在完成该矩阵前，不得把“静态/管理解包通过”描述成“真实安装通过”。
