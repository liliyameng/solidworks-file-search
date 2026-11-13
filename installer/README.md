# SwSearch 1.3.13 RC88 安装器

`Build-Release.ps1` 通过内部 `Build-Msi.ps1` 生成单一 per-machine x64 MSI，安装到 `ProgramFiles64\SwSearch`；直接调用 `Build-Msi.ps1` 会被拒绝，以防绕过冻结门禁。

- ProductCode：`{E5A97259-595F-4C23-8F9D-B5F423E7B69A}`
- 稳定 UpgradeCode：`{D9158356-29B3-44D6-A15A-ACDCE8328703}`
- 已发布 ProductCode（包括 RC85、RC86、RC87）均已退役，构建脚本拒绝复用。RC15 MSI 的固定门禁为 93,065,427 bytes、SHA256 `B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85`、ProductCode `{A6CCBEB3-300D-4D55-AEAD-B7AE975D6473}`、493 个 File 行；报告只对有单独历史基线的 sidecar 声称历史不变。

发布目录合并 App、Broker、Metadata Worker、PropertyViewer 四个 framework-dependent 入口和 Framework64 CLR4 编译的 AMD64 Taskpane DLL。独立 Indexer 入口不会进入 payload；Broker 是唯一 SQLite、发现与 Worker 所有者。

`Verify-Msi.ps1` 使用 WiX 反编译、CAB 导出、DTF 只读表查询和候选 App IL 门禁；它还从最终 MSI `Binary` 表提取并锁定 Coordinator/Bridge，先做 AMD64 PE、精确导出与依赖白名单检查，再在有界隔离进程中运行提取后的 Coordinator `--self-test` 和 Bridge 无效 MSI handle fail-closed gate。`Verify-Taskpane.ps1` 检查实际 CAB CLR4 AMD64 COM/ABI、目录探测调用图与隔离命名管道行为 harness。脚本不启动产品、SOLIDWORKS、Everything、Explorer、安装器或 `msiexec`。

正式候选只允许通过 `Build-Release.ps1` 构建。它在构建前快照受控文件，并在候选生成后由 `Verify-Msi.ps1` 独立复核 SHA256、文件集合和修改时间；任何失败都要求废弃当前 RC 身份。

RC88 使用新的 ProductCode `{E5A97259-595F-4C23-8F9D-B5F423E7B69A}`，并保持稳定 UpgradeCode。产品入口保持 framework-dependent .NET 8；Microsoft .NET 8 Desktop Runtime 与 Microsoft Edge WebView2 Evergreen Runtime 均为系统前置条件，不随安装包下载或内置。

RC88 已验证两项行为：云占位错误采用 1 小时持久冷却、queue due 与 autonomous lease，真实变化/交互请求强制旁路，等待重试保持 pending 且旧失败行可恢复；ResourceGovernor 对 read/write 分别 gate，避免读限速持锁阻塞写入。保持既有速率与并发限制，无速率上调、无实机测量；750ms 为慢提取阈值，压力冷却为 5 秒。

候选链是严格 zero-restore：所有 build/test/publish 都带 `--no-restore`，`Assert-FrozenRestoreGraph.ps1` 在各阶段核对精确 `project.assets.json`、dgspec、SDK/reference-pack 标识、唯一工作区 packageFolders 和依赖图。资产必须由受控的 `installer/NuGet.Offline.config` 与根级 `NuGet.config` 共同约束，只允许 `installer/.offline-feed` 本地源并使用根级 `.packages-rc48-r3`；锁定资产若声明用户 NuGet.Config、HTTP(S) 源或其他包目录，门禁会按设计停止。构建脚本不会自动 restore、访问网络或扫描用户全局 NuGet 缓存。

 Windows 11 sparse MSIX、生产签名、真实安装/升级/卸载和 SOLIDWORKS 进程内验证仍不在静态验收范围内。RC86 仅表示经静态门禁生成的候选，不代替这些目标环境验证。
