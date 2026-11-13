# Everything 文件发现、缓存与增量索引技术验证

> 已弃用：本文记录了早期包含 `.SLDDRW` 的方案。当前产品仅支持 `.SLDPRT` 和 `.SLDASM`；不要将本文的工程图发现或查询示例用于实现。

日期：2026-07-14  
状态：技术盘点完成；等待统一架构契约后再实施  
范围：仅 `.SLDPRT`、`.SLDASM`、`.SLDDRW`

## 1. 结论

推荐把 Everything 作为唯一的正常文件发现源，在它之上增加可替换的传输适配器和本地 SQLite 快照缓存：

1. 主适配器使用官方 Everything 1.4 SDK（与当前已安装的 1.4.1.1032 匹配），由应用按进程位数装载原生 DLL，通过 IPC 查询。
2. `es.exe` 只作为可选的进程外备选适配器；本机当前没有安装，不应成为隐式依赖。
3. Everything 未运行、IPC 被关闭、数据库未就绪或适配器不可用时，只读 SQLite 的最后一次成功快照并明确标记 `Stale`；没有缓存时返回可操作的 `Unavailable`，**不递归扫描磁盘**。
4. Everything 1.4 没有可供本项目可靠消费的变更日志 API。增量更新应采用“Everything 全量枚举三种扩展名 + SQLite 按代次差分”的方式；这里的“全量”是扫描 Everything 内存数据库，不是遍历文件系统。
5. 只有一次查询完整成功后，才提交该代次并判断删除。超时、IPC 中断、分页不完整时必须回滚，避免把大量文件误判为删除。

当前仓库没有源码、解决方案、`AGENTS.md` 或统一架构文档，Git 分支也尚无提交。因此本阶段没有实现公共接口、入口、UI、数据库迁移或测试工程，只新增本文档。

## 2. 本机验证证据

| 项目 | 结果 | 证据/含义 |
|---|---|---|
| OS | Windows x64，版本 `10.0.26200` | 满足 Everything 和 .NET Windows 进程要求 |
| Everything | `C:\Program Files\Everything\Everything.exe`，文件/产品版本 `1.4.1.1032` | 官方稳定版，当前 SDK 1.4 的直接目标 |
| 运行状态 | 用户进程 PID 31888，命令行为 `-startup`；`Everything` 服务为 Running/Auto | 后台索引服务与当前用户的 IPC 客户端均在运行 |
| IPC 配置 | `%APPDATA%\Everything\Everything.ini` 中 `ipc=1` | IPC 已启用 |
| IPC 窗口 | `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null)` 返回非零句柄 `0x30CD6` | Everything 1.4 的 Win32 IPC 端点实际存在 |
| 索引配置 | `index_date_modified=1`、`index_recent_changes=1`；NTFS 卷 C/D/E/F 被列入配置 | 当前机器可直接请求修改时间；实际结果仍须做空值/错误处理 |
| ES CLI | PATH、Everything 安装目录和已检查的常用安装位置均未发现 `es.exe`/`es64.exe` | 不能假定 CLI 可用 |
| SDK | Everything 安装目录及已检查的常用安装位置未发现 `Everything64.dll`、`Everything32.dll`、`Everything.dll`、头文件或导入库 | SDK 不是 Everything 主程序安装的附带组件；实施时须显式随应用部署或由安装器提供 |
| .NET | SDK `8.0.421`，SDK 内 MSBuild `17.11.48`；Windows Desktop Runtime 6/7/8/9 可用 | 可以构建 `net8.0-windows` 项目 |
| 独立原生工具链 | PATH 未发现独立 `msbuild`、`cl`、`cmake`、`ninja` | 若直接编译 SDK C 源码，需要另行确认 VS C++ Build Tools；优先部署官方预编译 DLL 可避免该依赖 |

本阶段没有安装、升级、停止或重启 Everything，也没有改动其配置。

官方资料：

- [Everything IPC/SDK](https://www.voidtools.com/support/everything/sdk/ipc/)：IPC 仅用于本机；官方提供 `Everything-SDK.zip`。
- [`Everything_Query`](https://www.voidtools.com/support/everything/sdk/everything_query/)：同步/异步查询语义及 `EVERYTHING_ERROR_IPC` 等错误。
- [ES 命令行接口](https://www.voidtools.com/support/everything/command_line_interface/)：列、日期格式、超时、实例名及退出码定义。
- [官方当前下载页](https://www.voidtools.com/downloads/)：Everything 1.4.1.1032、ES 和 SDK 是彼此独立的下载项。
- [Everything 1.5 SDK 说明](https://www.voidtools.com/forum/viewtopic.php?t=15853)：1.5 的 SDK3 使用命名管道；SDK 代际不可混用。

## 3. 建议模块边界与接口

接口名是给架构负责人的建议，不应在契约落定前直接成为公共 API。

```csharp
public interface IEverythingDiscovery
{
    ValueTask<DiscoveryHealth> ProbeAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<DiscoveredFile> EnumerateSolidWorksFilesAsync(
        DiscoveryQuery query,
        CancellationToken cancellationToken);
}

public sealed record DiscoveryQuery(
    int PageSize = 4096,
    bool IncludeSize = true,
    bool IncludeLastWriteTimeUtc = true);

public sealed record DiscoveredFile(
    string FullPath,
    SolidWorksFileKind Kind,
    long? SizeBytes,
    DateTimeOffset? LastWriteTimeUtc);

public enum DiscoveryState
{
    Ready,
    NotInstalled,
    NotRunning,
    IpcDisabled,
    DatabaseLoading,
    AdapterMissing,
    ArchitectureMismatch,
    QueryFailed
}

public interface IFileIndexStore
{
    ValueTask<IndexSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    ValueTask<RefreshResult> ReplaceFromAsync(
        IAsyncEnumerable<DiscoveredFile> files,
        CancellationToken cancellationToken);
}
```

建议内部再分三层：

- `EverythingSdk14Discovery`：官方 SDK 1.4 P/Invoke，负责页查询、请求字段、错误码翻译和 `Everything_Reset()` 生命周期。
- `EverythingEsDiscovery`：可选 `es.exe` 进程适配器，使用 UTF-8 CSV、FILETIME 日期格式、明确超时和退出码；不得通过 shell 拼接用户搜索词。
- `CachedFileDiscovery`：编排在线刷新与离线读取，返回数据同时携带 `Fresh/Stale/Unavailable`、最后成功刷新时间和失败原因。

不要把 `Everything.exe` GUI 程序当作 `es.exe`。也不要在发现失败时偷偷启动程序、修改 IPC 配置或重建 Everything 数据库；这些应由宿主策略或用户操作明确决定。

### 动态探测顺序

1. 根据当前进程架构选择 `Everything64.dll` 或 `Everything32.dll`；DLL 位数必须匹配应用进程，不以 Everything GUI 位数决定。
2. 安全装载应用自带、固定绝对路径下的 DLL，不从当前目录或任意 PATH 目录搜索，避免 DLL 劫持。
3. 调用 SDK 版本/数据库状态函数；把 DLL 缺失、BadImageFormat、IPC 不可用、DB 未加载分别映射为健康状态。
4. 若 SDK 1.4 不可用，再探测由配置明确给出的 `es.exe`，其次才是受信任的官方安装位置；本机当前会落到“CLI 不可用”。
5. 如果未来支持 Everything 1.5，新增 `EverythingSdk3Discovery`，先探测协议/实例名再选择 SDK3 命名管道；不要让 SDK 1.4 与 SDK3 共用一套 P/Invoke 声明。
6. 所有在线适配器失败后读取缓存。缓存存在则返回 `Stale`，不存在则返回 `Unavailable`；不枚举目录树。

进程内 SDK 是主选项：没有子进程和 CSV 临时文件，能分页并直接取请求字段。ES 的优点是崩溃隔离和部署简单，但进程启动、文本转义、超时、版本差异及大结果集解析成本更高，适合作为备选而不是默认路径。

## 4. Everything 查询约束

固定查询表达式应由代码生成，用户输入不能扩大文件类型范围。建议查询：

```text
file:ext:sldprt;sldasm;slddrw
```

查询后仍在托管代码中以 `OrdinalIgnoreCase` 白名单复核扩展名，拒绝目录和其他类型。请求字段只取：完整路径、文件名、大小、修改时间；首轮不请求高亮、内容、创建/访问时间等非必要属性。

SDK 1.4 是有状态 API。适配器应串行化对同一原生状态的访问，或为每个查询使用独立受控上下文；每轮查询前后调用 `Everything_Reset()`，不要让 UI 的取消、分页或并发搜索互相污染。同步 `Everything_Query(true)` 必须在线程池/后台线程执行并有上层超时；若改用异步 Win32 回复窗口，则把窗口生命周期完全封装在适配器内。

分页时记录 `GetTotResults`，逐页校验 offset、页数、结果数，直到完整接收。只有“所有页成功且计数一致”才将快照交给缓存提交。

## 5. 路径规范化与去重

保留 `full_path` 原文用于显示/打开，另生成 `path_key` 作为唯一键：

1. 拒绝空路径、相对路径和非白名单扩展名。
2. `Path.GetFullPath`，统一目录分隔符，移除非根路径末尾分隔符。
3. 统一普通盘符路径的盘符大小写；数据库唯一比较使用自定义 `OrdinalIgnoreCase` 规则或在写入前生成稳定的大小写折叠键。不要依赖 SQLite 默认 `NOCASE`，它只可靠覆盖 ASCII。
4. `\\?\C:\...` 与 `C:\...`、UNC 与 `\\?\UNC\...` 应折叠到同一种应用表示；不要用字符串替换破坏卷 GUID 路径。
5. 同一批结果先在内存按 `path_key` 去重，再批量 UPSERT。跨批次由唯一索引兜底。

默认只做“路径去重”。硬链接、符号链接和映射盘可能让同一实体有多条合法路径；是否按 `VolumeSerial + FileId` 合并属于产品语义，不能擅自决定。若以后要做物理文件去重，应把文件 ID 作为可空的辅助列，并保留所有可打开路径。

## 6. SQLite / FTS5 数据模型

建议数据库由索引模块私有管理，启用 WAL、`foreign_keys=ON`、合理的 `busy_timeout`，迁移带版本号。

```sql
CREATE TABLE index_state (
    singleton_id              INTEGER PRIMARY KEY CHECK (singleton_id = 1),
    schema_version            INTEGER NOT NULL,
    committed_generation      INTEGER NOT NULL DEFAULT 0,
    last_success_utc_ticks    INTEGER,
    source_version            TEXT,
    source_state              TEXT NOT NULL,
    last_error_code           TEXT
);

CREATE TABLE files (
    id                        INTEGER PRIMARY KEY,
    path_key                  TEXT NOT NULL UNIQUE,
    full_path                 TEXT NOT NULL,
    directory                 TEXT NOT NULL,
    file_name                 TEXT NOT NULL,
    extension                 TEXT NOT NULL CHECK (
                                  extension IN ('.SLDPRT','.SLDASM','.SLDDRW')),
    kind                      INTEGER NOT NULL,
    size_bytes                INTEGER,
    last_write_utc_ticks      INTEGER,
    metadata_state            INTEGER NOT NULL DEFAULT 0,
    first_seen_generation     INTEGER NOT NULL,
    last_seen_generation      INTEGER NOT NULL,
    is_present                INTEGER NOT NULL DEFAULT 1,
    removed_utc_ticks         INTEGER
);

CREATE INDEX ix_files_present_kind_name
    ON files(is_present, kind, file_name);
CREATE INDEX ix_files_last_write
    ON files(is_present, last_write_utc_ticks DESC);

CREATE VIRTUAL TABLE files_fts USING fts5(
    file_name,
    directory,
    content='files',
    content_rowid='id',
    tokenize='trigram'
);
```

`last_write_utc_ticks` 用 UTC `FILETIME`/ticks 的整数表示，避免文化格式和时区歧义；未知值存 `NULL`，不要伪装成 1970/1601。大小和修改时间变化都视为更新。FTS 与主表应在同一事务内由显式写入或经过测试的触发器同步。

`trigram` 更适合工程文件名/路径的任意子串和中文搜索。启动迁移时应实际试建临时 FTS 表验证运行时支持；若应用所带 SQLite 不支持 trigram，则退化为 `unicode61` 的词/前缀搜索，并在能力状态中暴露差异，不能静默改变搜索语义。

## 7. 首次索引、增量更新与删除检测

### 首次索引

1. 探测 Everything 与 DB 就绪状态。
2. 用固定扩展查询分页读取，建议页大小从 4096 起步，以基准测试调整。
3. 每批路径规范化、白名单复核、内存去重，然后使用准备语句批量 UPSERT；每 5,000～20,000 行一个短事务，避免单事务长时间锁库。
4. 使用临时 `seen_paths(path_key PRIMARY KEY)` 或本轮 generation 标记完整性；FTS 可在主表导入后一次重建，通常比逐行触发更快。
5. 全部页和计数校验成功后，在一个最终事务中更新 `committed_generation` 与 `last_success_utc_ticks`。

### 1.4 环境下的增量刷新

定时或由宿主事件触发再次执行同一个三扩展查询。对每条结果 UPSERT：

- 新 `path_key`：插入；
- 大小或 `last_write_utc_ticks` 变化：更新并刷新 FTS 所需字段；
- 未变化：只更新 `last_seen_generation`，避免无意义重写其他列。

完整查询成功后，将上一个已提交快照中 `last_seen_generation < current_generation` 的记录软删除：`is_present=0`、写入 `removed_utc_ticks`。这表示“已从 Everything 当前索引消失”，可覆盖真实删除、改名、移出索引范围等情况。保留软删除记录一段可配置时间便于诊断，之后再清理和增量 vacuum。

以下任一情况不得执行删除阶段：IPC 中断、超时/取消、数据库仍在加载、结果分页不完整、总数变化导致无法得到一致快照、SQLite 写入失败。可在刷新开始和结束各取一次总数；若不一致则有限次数重试或直接保留旧快照，不能无限循环。

不要把 `date_modified > high_watermark` 作为唯一增量依据：它检测不到删除/改名，时间戳可能回拨，也可能缺失。它可用于交互式的快速热更新，但仍需周期性 Everything 快照对账。

## 8. 离线、未启动和失败降级

| 场景 | 行为 |
|---|---|
| Everything/IPC Ready | 在线刷新；成功后原子发布新 generation |
| Everything 已安装但未运行 | 返回缓存 `Stale(NotRunning)`；提示用户启动，不自动递归扫描 |
| IPC 关闭/权限不匹配 | 返回缓存 `Stale(IpcDisabled/QueryFailed)`；给出设置/权限诊断 |
| Everything DB 加载中 | 短暂退避并受总超时限制；超时后返回旧缓存 |
| SDK DLL 缺失或位数错误 | 尝试已配置且受信任的 ES 适配器；否则返回旧缓存 |
| 首次使用且无缓存 | 返回空结果和 `Unavailable` 诊断，不能伪装成“未找到文件” |
| 查询中途失败 | 回滚本 generation，保留旧缓存，不做删除检测 |

健康状态和数据新鲜度应成为返回契约的一部分，避免 UI 把基础设施故障显示成零结果。

## 9. 可测试抽象与明确边界

### 单元测试（不依赖本机 Everything）

- `IEverythingDiscovery` 假实现产生分页、重复路径、大小写差异、长路径、UNC、中文、非法扩展和空元数据。
- 路径规范化与三扩展白名单的表驱动测试。
- generation 差分：新增、修改、未变、删除、改名。
- 查询第 N 页失败/取消/总数漂移时，旧 generation 不变且不软删除。
- SDK/ES 错误到 `DiscoveryState` 的一一映射。
- SQLite 临时文件测试：唯一约束、FTS 同步、进程重启后的持久性、迁移幂等。
- 假时钟验证 UTC 时间、缓存陈旧阈值和软删除保留期。

### 适配器契约测试

对 SDK 和 ES 适配器运行同一套黑盒契约：结果只含三个扩展、路径非空、取消可终止、失败不返回“完整成功”、分页无漏项/重复项。ES 测试必须验证参数数组传递而非 shell 字符串拼接，以及 UTF-8/FILETIME 解析。

### 本机集成冒烟（显式 opt-in）

标记为例如 `Category=RequiresEverything`，默认 CI 跳过。只读取现有 Everything 索引并验证 IPC 版本、DB Ready、固定查询和少量结果字段。离线测试通过缺失 DLL、错误实例名或 fake transport 模拟；**不要为了测试停止用户的 Everything 服务**。测试不得创建/遍历全盘，也不得修改 Everything 配置或触发 reindex。

### 性能测试

- 分开测量 Everything 查询首项/全量耗时、托管规范化、SQLite UPSERT、FTS 构建和离线查询。
- 用合成 10 万/100 万行缓存数据做可重复基准；真实 Everything 基准仅报告环境信息，不作为 CI 硬阈值。
- 观测峰值内存、每批事务耗时、数据库增长和取消延迟。目标阈值应由架构/产品契约决定，本阶段不擅自承诺绝对毫秒数。

## 10. 风险与待架构确认项

1. **SDK 部署与许可审查**：SDK DLL 当前未安装；需确认随应用部署的位数、来源校验、更新与许可证归档方式。
2. **目标进程位数**：若 SOLIDWORKS 插件必须 x64，则只需 x64 SDK；若另有 AnyCPU 工具进程，必须固定首选架构或同时部署两个 DLL。
3. **Everything 1.5 路线**：当前机器是 1.4。未来升级 1.5 时需独立 SDK3 适配器和实例名策略，不能只替换 DLL。
4. **缓存搜索语义**：需确认离线搜索要求是 token/prefix 还是任意子串，以最终确定 FTS tokenizer。
5. **路径与物理实体语义**：映射盘、UNC、硬链接是否显示为多个结果需产品决定。
6. **刷新触发与陈旧 SLA**：由宿主决定启动刷新、周期刷新和手动刷新；索引模块只提供可取消操作和状态。
7. **大规模消失保护**：可增加“消失比例超过阈值先保留旧快照并报警”的保险，但阈值必须契约化，不能掩盖真实批量删除。
8. **数据库归属与加密**：缓存路径、每用户/每机隔离、ACL、是否包含敏感工程路径，需要统一架构决定。

## 11. 后续实施切片（收到架构契约后）

1. 建立内部契约、健康状态与 fake transport，不接 UI。
2. 实现并测试 SQLite schema、generation 差分和路径规范化。
3. 引入经过校验的官方 SDK 1.4 x64 资产，实现 P/Invoke 适配器与 opt-in 冒烟测试。
4. 视部署决定补充 ES 备选适配器。
5. 做真实索引基准，调整分页/批次，再由架构层接入公共入口。

当前阶段到此停止，等待统一架构契约和模块边界确认。

## 12. 架构契约落地后的实现结果

2026-07-14 后续阶段已按 `docs/ARCHITECTURE.md` v1 实施：

- `SwSearch.Everything` 已实现无 SDK DLL 路径的 Everything 1.4 Query2W / `WM_COPYDATA` 异步传输，使用消息窗口接收分页结果，并校验字段 flags、偏移和消息边界。
- 发现查询固定为 `file:ext:sldprt;sldasm;slddrw`，不接受用户控制的 Everything 语法；结果再次按扩展名白名单过滤。
- `SwSearch.Indexer` 已引入锁定版本 `Microsoft.Data.Sqlite 8.0.28`，实现 schema v1、WAL、FTS5 trigram、成功 generation 差分、软删除、元数据队列/租约恢复、direct/effective 属性和工程图反向刷新。
- 本地调用面为 `SqliteIndexStore`（`IIndexStore`）和 `IndexSearchService`（`ISearchService`）；UI 不需要也不应直接拼 SQL。
- 本机真实只读冒烟查询到 145,691 个三类文件；首批 128 条均成功映射大小和修改时间。临时数据库首次完整刷新约 24.6 秒。该数据量下重点优化项仍是 staging/FTS 构建，但当前阶段按总控要求以正确性为先，不再继续无上限优化。
- Everything 关闭/IPC 不可用时，`IndexingCoordinator` 不调用发现枚举，只把缓存标记为 stale 并继续只读搜索；没有目录遍历降级。

实现仍依赖 Metadata Worker 提供实际 `DocumentMetadata`。索引器已经提供队列租约、元数据 UPSERT、引用绑定和继承物化能力，但不在本模块内读取 SOLIDWORKS 文件或启动 SOLIDWORKS。
