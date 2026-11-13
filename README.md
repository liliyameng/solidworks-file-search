# SOLIDWORKS 文件检索 v1.3.13（RC88）

Windows x64 中文桌面检索工具，仅处理 `.SLDPRT` 和 `.SLDASM`。Broker 持有 SQLite schema、文件发现、Everything 集成和 Metadata Worker；主程序的交互搜索与任务窗格、属性查看器一样是 Broker 的客户端，但搜索结果页通过 `SwSearch.Integration` 的只读回退直接读本地 SQLite 缓存（每次查询独立开只读连接、不池化，保证侧车可跨进程重建），管道仅承载进度/状态/刷新等控制消息。

## 核心行为

- stale-while-revalidate：Broker 先开放 IPC 和 SQLite 缓存查询，再异步发现文件并更新属性；已有缓存立即显示。
- Everything 式即时搜索：输入、模式和目录范围变化自动触发（工具栏 JS 40 ms 防抖 + ViewModel 80 ms 防抖双层），回车立即执行；IME 组合期间不提交半成品，旧响应不能覆盖最新输入，空查询列出缓存文件。原生 Shell 结果快照在输入静止约 200 ms 后才开始全量填充。
- SOLIDWORKS Taskpane 提供明确的“全电脑/文件夹”范围；文件夹只做纯语法规范化并严格匹配当前目录及子目录。离线 UNC 或当前不存在的目录仍查询该范围的历史缓存且绝不回退全电脑；空、相对或语法非法路径不发请求。浏览对话框不会自动导航持久化路径。
- SQLite 初始化专用连接允许建库；初始化后的所有读写连接固定为 `ReadWrite`，主库在检查与打开之间消失也只报告 `INDEX_DATABASE_MISSING`，绝不重建空库。
- 搜索结果 revision 只在已提交的文件集合、路径、文件名、属性或配置真实变化时增加；队列提权、租约和重试调度不会发布 revision。
- 连接有界失败、明确重试、断线自动重连；断线期间保留最后结果和最新输入。
- Everything 仅作 Broker 侧文件发现加速器；缺失时透明回退到内置发现，查询始终走本地缓存/FTS。
- App、Viewer、Taskpane 不引用 Indexer 或 SQLite；Broker 是唯一数据库与索引所有者。

数据默认位于 `%LOCALAPPDATA%\SwSearch`。自动化测试必须通过 `SWSEARCH_DATA_ROOT` 使用隔离临时目录。

## 构建与静态验证

```powershell
dotnet build .\SolidWorksSearch.sln -c Release
dotnet test .\SolidWorksSearch.sln -c Release
powershell -ExecutionPolicy Bypass -File .\installer\Publish.ps1
powershell -ExecutionPolicy Bypass -File .\installer\Build-Release.ps1
```

正式候选必须使用 `installer\Build-Release.ps1` 一次性生成；该入口在 MSI 生成后以 SHA256、文件集合和 UTC 时间戳复验所有受控源码、测试、安装脚本与项目文件。

当前代码为 RC75 候选前审计状态；`artifacts\SwSearch-x64-v1.3.9-rc75.msi` 尚未生成。RC1 至 RC74 均已退役并仅保留追溯；历史固定 MSI 不得覆盖。构建与静态验收不启动 SwSearch GUI、SOLIDWORKS、Everything、Explorer、安装器或 `msiexec`，不修改真实注册表、用户配置或 `%LOCALAPPDATA%\SwSearch`。

## 尚未完成

RC12 不包含签名的 Windows 11 sparse MSIX，不代表生产签名完成，也不代表真实安装、SOLIDWORKS 2024 进程内或实机验证完成。相关人工验收边界见 [最终验收说明](docs/FINAL-ACCEPTANCE.md)。

## 仓库内容与本地缓存（GitHub 发布版）

- `docs/项目记忆/`：项目记忆快照（`--no-restore` 冻结恢复图规则、WebView2 共享用户数据目录约束等），为 ZCode 会话记忆的只读副本。
- 以下目录属于本机构建状态，体积过大，**不入库**（已列入 `.gitignore`）：`.nuget/`、`installer/.offline-feed/`、`installer/offline-feed/`、`installer/.build/`、`tests/installer/sandbox/`、`.packages-*/`。
- `NuGet.config` 仅指向本地离线包源 `installer\.offline-feed`，这是冻结恢复图保护的一部分，**保持原样不改**。新克隆环境如需自行构建，请临时添加 nuget.org 包源或重建离线源后再 `dotnet restore`；原开发机继续使用 `--no-restore` 直连冻结包目录。

