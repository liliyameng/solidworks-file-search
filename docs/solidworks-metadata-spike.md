# SOLIDWORKS 元数据与引用关系技术盘点

> 已弃用：本文记录了早期工程图引用继承方案。当前产品仅支持 `.SLDPRT` 和 `.SLDASM`，不再提取工程图、解析其引用关系或为其注册属性查看器入口。

> 状态：统一架构契约后的首版实现与验证记录  
> 日期：2026-07-14  
> 范围：`.SLDPRT`、`.SLDASM`、`.SLDDRW` 的只读元数据提取、工程图引用继承、缓存，以及 Windows 文件资源管理器右键属性 Viewer 需要的统一服务接口。

## 1. 结论

1. 推荐的完整离线提取器是 SOLIDWORKS Document Manager API（DM）。它能在不安装/不启动 SOLIDWORKS 主程序的情况下读取文档、配置、属性和引用，但官方明确要求有效的 DM license key，不能把“COM 已注册”当成“DM 已授权”。[官方概述](https://help.solidworks.com/2024/English/api/swdocmgrapi/Welcome-swdocmgrapi.html)
2. 本机已确认安装 SOLIDWORKS 2024 SP0.1，主程序 COM 和 DM COM 均已注册；只验证了 DM class factory 可以实例化，没有调用 `GetApplication(key)`、没有验证 key、没有对真实文件做 DM 打开测试。因此当前 DM 能力状态必须记为 `Unknown/LicenseNotProbed`，不能写成 `Available`。
3. 无 DM key 时仍需可用：Viewer 先读缓存；未命中则使用 Windows Property System/文件基础信息的只读 provider，至少展示文件名、路径、类型、大小、修改时间以及系统属性处理器实际能提供的字段。字段不完整时返回 `Partial` 和明确原因，不伪造“无属性”。
4. 不采用第三方库直接解析 SOLIDWORKS 私有二进制流作为生产主路径。通用 Compound File 库最多能读取标准属性集或做诊断，不能保证跨版本正确解析自定义属性、解析值、配置和引用关系。
5. Explorer Viewer 的硬约束是：不启动可见 SOLIDWORKS，且默认不创建任何 `SldWorks.Application` 进程。桌面 COM 兜底仅保留为 Viewer 之外、显式允许的后台补全能力；Explorer 请求的策略固定为 `SolidWorksFallback = Never`。
6. 工程图必须建立 `drawing -> referenced model` 边。搜索文档既包含工程图自身属性，也包含所引用模型的文件级/配置级继承属性。标题栏来源应优先精确映射到每张 sheet 的 `CustomPropertyView`，不能仅靠工程图文件名或把任意第一个引用模型当成标题栏来源。

## 2. 已确认事实与证据

### 2.1 仓库

- 工作区 `C:\Users\liliy\Documents\solidworks插件` 是尚无提交的空仓库。
- 盘点时没有 `AGENTS.md`、统一架构文档、公共入口、源代码、测试或 SOLIDWORKS 样本文件。
- 因此本阶段只新增本文档，不实施模块、不修改 Shell/UI。

### 2.2 本机

| 项目 | 已确认结果 | 结论边界 |
| --- | --- | --- |
| OS | Windows 11 Pro x64，版本 `10.0.26200` | 可以设计 Windows COM/Property System provider |
| .NET | SDK `8.0.421`；Windows Desktop Runtime 6/7/8 均存在 | 具体目标框架等待统一契约 |
| SOLIDWORKS | 2024 SP0.1，程序版本 `32.0.1.0004` | 安装存在，不代表任意部署机都有 |
| 主程序 COM | `SldWorks.Application`，CLSID `{AFBEC3B2-B1A6-4908-B608-D97D2AAB5498}` | 仅证明注册存在 |
| DM COM | `SwDocumentMgr.SwDMClassFactory`，CLSID `{00AB5D8D-2B8F-416B-9761-92FACC8872BE}` | class factory 实例化成功，不证明 key 有效 |
| Interop | `SolidWorks.Interop.sldworks.dll`、`swconst.dll`、`swdocumentmgr.dll` 版本 `32.0.1.4` | 实施时不可依赖硬编码安装路径 |
| 运行进程 | 盘点时已有一个 `SLDWORKS.exe` 在运行 | 本次盘点未启动、附加或控制该进程 |
| 样本 | 工作区无 `.SLDPRT/.SLDASM/.SLDDRW` | 尚未完成真实文件端到端验证 |

使用过的只读检查包括卸载项、ProgID/CLSID、二进制版本和进程查询。唯一一次 COM 实例化仅创建并立即释放 `SwDMClassFactory`；未传 license key，未创建 `ISwDMApplication`，未读写用户文档。

### 2.3 官方 API 边界

- DM 是独立 COM 组件，支持文档和配置读写，但必须取得 license key。[DM Welcome](https://help.solidworks.com/2024/English/api/swdocmgrapi/Welcome-swdocmgrapi.html)
- DM 打开错误能区分无授权、未来版本、文件不存在和一般失败；`NoLicense` 也可能表示 key 不覆盖文件保存版本。[`SwDmDocumentOpenError`](https://help.solidworks.com/2024/english/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.SwDmDocumentOpenError.html)
- DM 可读取工程图全部外部引用及 broken 状态；该 API 对老版本文件的状态完整性有限。[`ISwDMDocument21.GetAllExternalReferences5`](https://help.solidworks.com/2023/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMDocument21~GetAllExternalReferences5.html)
- DM drawing view 暴露引用文档和引用配置。[`ISwDMView` members](https://help.solidworks.com/2024/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMView_members.html)
- 对 SOLIDWORKS 2017+ 保存的工程图，`ISwDMSheet4.CustomPropertyView` 可给出该 sheet 用作自定义属性来源的 drawing view。[`CustomPropertyView`](https://help.solidworks.com/2024/english/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMSheet4~CustomPropertyView.html)
- 配置级属性确实独立于文件级自定义属性；标题栏/表格使用配置属性时，若配置属性缺失再使用文件级属性。[官方配置属性说明](https://help.solidworks.com/2024/english/solidworks/sldworks/r_Custom_Properties_in_Configurations.htm)、[标题栏配置属性](https://help.solidworks.com/2024/english/SolidWorks/sldworks/t_title_block_configs.htm)
- DM 返回的 linked property 解析值是文件最后一次由 SOLIDWORKS 保存时计算出的值，并非实时重算。[文件级 `GetCustomProperty`](https://help.solidworks.com/2021/English/api/swdocmgrapi/SOLIDWORKS.Interop.swdocumentmgr~SOLIDWORKS.Interop.swdocumentmgr.ISwDMDocument~GetCustomProperty.html)、[配置级 `GetCustomProperty2`](https://help.solidworks.com/2021/English/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMConfiguration14~GetCustomProperty2.html)
- 桌面 API 的 `OpenDoc6` 支持 silent/read-only 和错误/警告码，但打开父文档还可能加载引用文档。因此它不是 Explorer Viewer 的默认安全路径。[`ISldWorks.OpenDoc6`](https://help.solidworks.com/2024/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ISldWorks~OpenDoc6.html)

## 3. 能力分层与安全降级

### 3.1 Provider 顺序

| 优先级 | Provider | 能力 | Viewer 是否允许 |
| --- | --- | --- | --- |
| 0 | `IMetadataCache` | 返回同一文件指纹下的完整/部分快照 | 是，必须首先尝试 |
| 1 | `DocumentManagerExtractor` | 文件级、配置级、raw/resolved 属性；drawing sheet/view/reference | 是，但仅在运行时探测为 `Available` 后 |
| 2 | `WindowsPropertyStoreExtractor` | Windows 属性处理器能暴露的字段，能力依机器而异 | 是，无 key 的主要按需降级 |
| 3 | `BasicFileExtractor` | 路径、扩展名、大小、时间、文件存在/访问状态 | 是，保证有结构化结果 |
| 4 | `SolidWorksComExtractor` | 可由桌面 API补全/重算 | Explorer 固定禁止；仅为未来显式后台任务预留 |

组合规则不是“一个 provider 失败则整体失败”。基础字段始终可合并；高保真 provider 失败后结果可为 `Partial`，并在 `ProviderAttempts` 保留 `LicenseMissing`、`FutureVersion`、`Timeout` 等原因。

### 3.2 运行时能力探测

`DocumentManagerExtractor.ProbeAsync` 应按下列有限步骤执行，并短期缓存结果：

1. 判断 Windows/x64、ProgID 和兼容 interop 是否可解析。
2. 从部署配置的 secret provider 取得 key；不存在即返回 `LicenseMissing`。key 不进入仓库、缓存、日志、异常文本或遥测。
3. 创建 class factory，调用 `GetApplication(key)`；空对象或 COM 异常映射为 `LicenseInvalid/ProviderUnavailable`。
4. 如有受控 smoke fixture，以只读方式打开并记录 `SwDmDocumentOpenError`。不能把“文件版本超出 key 覆盖范围”混成“文件损坏”。
5. 探测有硬超时，运行在隔离 STA worker 中；超时销毁 worker，不在调用线程继续等待失控 COM。

本机盘点只完成步骤 1 的一部分。其余均为运行时未知。

### 3.3 无 key 的明确行为

- 缓存命中：立即显示缓存，并标记提取时间和文件指纹；不触发 COM。
- 缓存未命中：依次调用 Windows Property Store 和基础文件 provider。
- 能读取多少显示多少；顶部状态显示“部分属性（Document Manager 未授权/不可用）”，而不是空白或无限 loading。
- 工程图若缺少可用的引用解析能力，返回 `References.status = Unavailable`，不猜测同名 `.SLDPRT/.SLDASM`。
- 后续后台索引若在有 key 的机器上生成了丰富缓存，Viewer 可直接消费；缓存必须记录来源和新鲜度。

### 3.4 桌面 COM 兜底边界

为兼容原始需求保留抽象，但默认策略如下：

- Explorer Viewer：`Never`，不创建、不附加 `SldWorks.Application`，不打开文档。
- 后台补全任务：只有统一架构明确授权并由用户/管理员启用才可用；必须运行在独立 STA 进程，设置应用/文档不可见，使用 silent + read-only，记录自己打开的文档并只关闭这些文档。
- 不得退出或改变用户已有的 SOLIDWORKS 会话；如果 worker 自己启动实例，才可在完成后退出该实例。
- timeout 时只终止 worker 自己拥有的进程，绝不 kill 用户原有 `SLDWORKS.exe`。

## 4. 统一元数据接口建议

接口属于 Core，Shell、Viewer、索引器只依赖它，不引用任何 SOLIDWORKS interop：

```csharp
public interface ISolidWorksMetadataService
{
    ValueTask<MetadataResult> GetAsync(
        MetadataRequest request,
        CancellationToken cancellationToken);
}

public sealed record MetadataRequest(
    string Path,
    MetadataPurpose Purpose,       // Viewer, Indexing, BackgroundEnrichment
    bool IncludeConfigurations,
    bool IncludeDrawingReferences,
    TimeSpan Timeout,
    CachePolicy CachePolicy,
    SolidWorksFallbackPolicy SolidWorksFallback);

public sealed record MetadataResult(
    MetadataStatus Status,         // Success, Partial, Failed, Timeout, Cancelled
    DocumentMetadata? Document,
    IReadOnlyList<ProviderAttempt> ProviderAttempts,
    MetadataError? Error,
    bool FromCache,
    DateTimeOffset ExtractedAtUtc);
```

建议再抽出可替换、可 mock 的内部端口：

```csharp
public interface IMetadataExtractor
{
    string Id { get; }
    ValueTask<CapabilityResult> ProbeAsync(CancellationToken ct);
    ValueTask<ExtractionResult> ExtractAsync(
        ExtractionRequest request, CancellationToken ct);
}

public interface IMetadataCache
{
    ValueTask<CacheLookup> TryGetAsync(FileIdentity file, CancellationToken ct);
    ValueTask PutAsync(MetadataSnapshot snapshot, CancellationToken ct);
    ValueTask InvalidateDependentsAsync(FileIdentity model, CancellationToken ct);
}
```

`Purpose = Viewer` 的 service policy 强制覆盖来访参数为短超时、缓存优先、`SolidWorksFallback = Never`，避免 Shell/UI 绕过安全边界。

## 5. 数据模型

### 5.1 文档与属性

```text
DocumentMetadata
  path, canonicalPath, kind, fileIdentity, fileVersion?
  fileProperties[]
  configurations[]
    name, isActiveOrLastSaved?, properties[]
  drawingSheets[]
  references[]
  searchableFields[]
  completeness, warnings[]

PropertyValue
  name, normalizedName, type
  rawValue, resolvedValue
  scope: File | Configuration | Drawing | InheritedModel
  configuration?
  sourcePath, sourcePropertyName
  isLinked?, isResolved?, evaluatedAtSave
  providerId, confidence
```

必须同时保留 `rawValue` 和 `resolvedValue`。搜索默认索引两者，展示默认优先 resolved，并允许展开查看 raw/link 信息。DM 的 resolved 值标记为 `evaluatedAtSave = true`，避免暗示它已按当前外部条件实时重算。

物料编码和名称不硬编码成唯一中文字段。使用可配置、大小写不敏感、保留原名的 alias 集，例如：

```text
PartNumber: 物料编码, 零件号, 图号, 代号, PartNumber, Part Number, Number
Name:       名称, 零件名称, 品名, Description, Title
```

配置属性解析优先级：指定/引用配置的属性 > 模型文件级属性。缺失才回退，不用空字符串覆盖有效的文件级值；同时保留每个原始来源，便于解释命中。

### 5.2 工程图引用边

```text
DrawingReference
  drawingPath
  sheetName?
  viewName?
  referencedModelPath
  referencedConfiguration?
  isCustomPropertyView
  referenceStatus: Resolved | Missing | Broken | Ambiguous | Unknown
  source: SheetCustomPropertyView | DrawingView | ExternalReference
```

提取流程：

1. 读取 drawing 的直接属性。
2. 枚举 sheet/view；对每个 view 记录 `ReferencedDocument` 和 `ReferencedConfiguration`。
3. 对 2017+ 文件读取 `ISwDMSheet4.CustomPropertyView`，将该 view 标为该 sheet 的标题栏模型属性首选来源。
4. 再用 `GetAllExternalReferences5` 补齐视图之外的引用、missing/broken 状态；路径解析应记录命中规则，不静默改写原始路径。
5. 对每条已解析模型边提取目标配置属性与文件级属性，并生成带来源的 `InheritedModel` 字段。
6. 工程图搜索索引收录所有已解析 view model 的继承字段；标题栏“编码/名称”展示则优先 `isCustomPropertyView = true` 的模型/配置。
7. 当老文件不支持 `CustomPropertyView`、sheet 无 view、派生视图没有直接引用或多个模型冲突时，不任意选一个：所有候选仍可搜索，展示状态为 `Ambiguous`，候选来源可展开查看。

由此，搜索模型物料编码或名称可以命中引用该模型的 `.SLDDRW`。若不同 sheet 引用不同模型，各 sheet 来源分别保留，不能扁平覆盖成单值。

## 6. 缓存与失效

首版可采用 SQLite，但存储实现等待架构契约。逻辑 key 至少包含：

```text
canonical path + volume/file id（可得时） + length + lastWriteTimeUtc
+ schemaVersion + extractorVersion
```

- 快路径仅比较 identity/length/mtime；后台可对碰撞或高风险场景追加内容 hash。
- snapshot 原子写入，旧的成功快照不能被一次临时 timeout 覆盖。
- 保存正向 `drawing -> model` 和反向 `model -> drawings` 边。模型指纹变化时，失效其属性快照及所有 drawing 的继承搜索字段。
- reference missing/permission denied 使用短 TTL 的 negative cache；license/provider unavailable 使用能力 TTL，不能永久缓存。
- 缓存中每个字段保留 provider、提取时间、完整度和 source path/config，支持 Viewer 解释数据来源。
- Viewer 打开时：有效缓存立即返回；可选异步刷新只能在架构提供可靠通知机制后加入，不能让 Explorer 调用线程等待长 COM。

## 7. 错误、超时与兼容性

建议错误码稳定、provider 无关，原始 HRESULT/DM enum 仅放诊断字段：

| Code | 含义与 UI 行为 |
| --- | --- |
| `NotFound` | 文件已移动/删除 |
| `UnsupportedType` | 不是三类受支持扩展 |
| `AccessDenied` | 无读权限；不重试风暴 |
| `SharingViolation` | 文件被占用；允许短 TTL 重试 |
| `LicenseMissing` / `LicenseInvalid` | DM 不可用，继续降级并显示部分结果 |
| `FutureVersion` | DM/其 key 不支持该文件版本；不是损坏 |
| `CorruptOrUnreadable` | provider 判定无法解析；保留基础字段 |
| `ReferenceMissing` / `ReferenceBroken` | 主文档仍可展示，引用边带状态 |
| `ProviderUnavailable` | COM 未注册/位数不符/interop 不兼容 |
| `Timeout` | 返回已有部分数据，隔离并回收 worker |
| `Cancelled` | Viewer 已关闭或上游取消 |

COM provider 使用独立 x64 STA worker，所有调用设总 deadline；进程间协议只传 DTO，不传 RCW。每个 COM 对象在 worker 内确定性释放。损坏文件、COM crash 或不可取消调用不得拖死 Explorer/Viewer 主进程。

## 8. 文件格式与第三方库判断

- SOLIDWORKS 原生文件含私有、版本相关的数据。即使底层可被通用 Compound File/structured-storage 库打开，也不等于能可靠解释配置、自定义属性解析值和引用图。
- OpenMcdf 等库可作为“标准 SummaryInformation/DocumentSummaryInformation 是否存在”的实验工具，不能作为完整元数据契约；任何启发式结果必须标 `LowConfidence` 且默认不进入继承搜索字段。
- Windows Property System 借助已安装的属性处理器，部署差异大但适合无 key 的安全降级；必须运行时枚举实际返回字段，不能承诺完整配置/引用能力。
- eDrawings/File Utilities 的安装不能替代 DM license，也不应通过 UI 自动化抓文字。
- 不直接修改原文件。即使 DM 支持写操作，本项目 provider 按最小权限只读打开。

## 9. Explorer Viewer 调用约定

Shell command 只负责把一个规范化绝对路径传给独立 Viewer 进程；不在 `explorer.exe` 内加载 SOLIDWORKS interop、数据库或长驻 COM。

```text
Explorer context command
  -> Viewer.exe --path <absolute-path>
  -> ISolidWorksMetadataService.GetAsync(Purpose=Viewer, timeout=短, cache-first)
  -> 立即显示 Loading/缓存结果
  -> DM（仅 capability=Available）或无-key providers
  -> Success / Partial / Timeout / Failed + 可解释 provider 状态
```

Viewer 页面分区：

1. 文件信息；
2. 文件级自定义属性（raw/resolved）；
3. 配置列表及配置属性；
4. 工程图 sheet/view -> 模型/配置关系；
5. 从模型继承的可搜索属性，明确显示来源；
6. 完整度、缓存时间、错误/超时详情。

Shell 注册、菜单文案“查看 SOLIDWORKS 属性”、进程单实例策略和 UI 技术栈等待统一架构契约，本阶段不创建注册表项或公共入口。

## 10. 测试策略与验收样本

### 10.1 单元测试（不依赖 SOLIDWORKS）

- provider 探测与选择：DM 缺 key/无 COM/超时后正确降级。
- raw/resolved、属性 alias、配置优先级、空值回退。
- drawing graph：单 sheet 单模型、多 sheet 多模型、多个 view、missing/broken、标题栏来源 ambiguous。
- 搜索继承：模型编码/名称能命中 drawing，且能解释 source path/config/property。
- 缓存：命中、指纹变化、模型变化导致 drawing 反向失效、negative TTL。
- timeout/cancellation：返回已有部分结果，worker 崩溃不影响调用进程。

### 10.2 受控集成测试（后续提供 fixture）

至少准备：

- part：文件级属性、两个配置、linked dimension/mass property、中文属性名；
- assembly：组件引用、缺失组件、不同引用配置；
- drawing：标题栏 `$PRP` 与模型来源字段、`CustomPropertyView`、多 sheet/多模型、派生/剖视图；
- 旧版保存文件、2024 文件、未来版本文件（如可获得）、只读/占用/损坏副本；
- 同一 fixture 分别在有效 key、无 key、无 SOLIDWORKS 安装环境运行。

验收重点：

1. 有 key 时，Viewer 不启动 SOLIDWORKS 即展示完整属性和工程图继承字段。
2. 无 key 且无缓存时，Viewer 快速展示可得基础/系统属性，并明确 `Partial: LicenseMissing`。
3. 搜索模型的物料编码/名称能够命中 drawing。
4. 任一 provider 卡死不拖死 Viewer；达到 deadline 后得到 `Timeout/Partial`。
5. 全链路不写用户 `.SLD*` 文件，不泄漏 key，不 kill 用户 SOLIDWORKS。

## 11. 建议模块边界（等待契约确认）

```text
Metadata.Core
  contracts, DTO, aliases, merge/search inheritance rules
Metadata.Cache
  snapshot + dependency graph repository
Metadata.Provider.DocumentManager
  DM capability probe and read-only extractor
Metadata.Provider.WindowsPropertySystem
  no-key partial extractor
Metadata.Provider.BasicFile
  guaranteed baseline
Metadata.Provider.SolidWorksCom       (optional, never used by Viewer)
Metadata.Worker
  x64 STA isolation and timeout boundary
```

Core 不引用 COM；provider 不引用 Viewer/Shell；缓存不持有 COM 对象；Viewer 只引用 service contract。统一架构契约出现后，应按其命名、DI、错误模型、缓存和项目边界映射，本文接口是建议而非抢先落地的公共契约。

## 12. 风险与待确认事项

| 风险/未知 | 影响 | 处理 |
| --- | --- | --- |
| DM key 是否存在、覆盖哪些保存版本 | 完整离线能力未知 | 运行时 probe；不记录 key；按文件返回 `NoLicense/FutureVersion` |
| 无真实 fixture | 端到端能力尚未验证 | 架构确认后先建最小 fixture matrix 与 gated integration tests |
| 2017 前 drawing 无 `CustomPropertyView` API | 标题栏来源可能不唯一 | 所有引用可搜索，标题栏来源标 `Ambiguous` |
| linked resolved 值可能陈旧 | 展示值与当前模型状态可能不同 | 保留 raw/resolved，标 `evaluatedAtSave` 与文件 mtime |
| 引用路径移动/同名冲突 | 可能关联错模型 | 保留原始路径、解析规则和状态；不靠同名猜测 |
| Windows Property Store 字段因机器不同 | 无 key 结果不一致 | capability-driven、字段带 provider/completeness |
| COM 卡死/崩溃 | Explorer/Viewer 可用性 | 独立 STA worker、总 deadline、只杀自有 worker |
| 桌面 COM 可能加载引用/占用许可 | 干扰用户会话 | Viewer 禁止；仅显式后台能力可开启 |

## 13. Spike 阶段改动与复核

- 已改文件：仅 `docs/solidworks-metadata-spike.md`。
- 未改：公共入口、UI、Shell 注册、项目文件、用户 SOLIDWORKS 文件、git 历史。
- 复核命令：

```powershell
git status --short
Get-Content -Raw .\docs\solidworks-metadata-spike.md
```

该触发条件已满足；实现结果见下一节。

## 14. 契约落地后的实现结果（2026-07-14）

### 14.1 已实现

- `swsearch.metadata.v1` 当前用户限定命名管道：`PipeOptions.CurrentUserOnly`、单 server instance/顺序处理、4 字节 little-endian 长度前缀 UTF-8 JSON；请求上限 64 KiB、响应上限 4 MiB。
- `MetadataPipeClient` 提供纯管道 client；`MetadataWorkerClient` 提供可被 Indexer/Viewer 引用的 `IMetadataExtractionClient` 实现。无 host 时以 `UseShellExecute=false`、`CreateNoWindow=true` 启动 worker；transport crash/timeout 后只终止并重建自己启动的 worker，不处理用户其他进程。
- 请求验证：协议版本、非空 request id、绝对本地/UNC 路径、最大 32767 字符、三种扩展名、存在性；拒绝 `\\?\`、`\\.\`、`\??\`、`GLOBALROOT` 设备路径和 NUL。
- worker 默认 5 秒、最大 15 秒；提取运行在隔离 background STA thread。COM 不可取消调用达到 deadline 后返回 `METADATA_TIMEOUT` 并让 host 退出，由 client 重建进程。
- DM 使用 `SwDocumentMgr.SwDMClassFactory` late binding；生产程序集不引用 SOLIDWORKS interop。`IDocumentManagerKeyProvider` 与 Credential Manager provider 只读取 `SwSearch/DocumentManagerLicenseKey`，key 不进入命令行、JSON、日志或错误消息。
- DM 只读打开，提取文档级属性、配置名、配置级属性、raw/linked expression、resolved/evaluated value；工程图从 views 取得引用模型和引用配置，并用 `GetAllExternalReferences5` 补齐引用和 broken 状态。
- 无 DM/key/读取失败时使用 Windows `IPropertyStore`，再降级到 `FileCandidate` 文件系统信息。fallback 始终带 `MetadataSource` 和 warning code，不把空属性伪装成“文件没有属性”。
- 全部错误使用稳定 code + 中文消息；已覆盖协议、路径、权限、共享冲突、损坏、future version、key 缺失/无效、property store、取消、超时和 worker crash。
- 明确没有任何 `SldWorks.Application` ProgID、主程序实例化或 SOLIDWORKS 隐藏/可见启动路径。

### 14.2 最小公共契约变更

`DocumentMetadata` 末尾新增可选字段：

```csharp
IReadOnlyList<string>? ConfigurationNames = null
```

原因：原 DTO 只能从配置属性间接推断配置名，不能表示“配置存在但没有自定义属性”。字段位于 record 末尾且有默认值，旧构造调用和 v1 JSON consumer 可继续工作，因此没有提升 `ContractVersions.MetadataPipe`。

### 14.3 测试证据

后台验证命令：

```powershell
dotnet test tests\SwSearch.Metadata.Worker.Tests\SwSearch.Metadata.Worker.Tests.csproj -c Release
dotnet build SolidWorksSearch.sln -c Release
src\SwSearch.Metadata.Worker\bin\Release\net8.0-windows\SwSearch.Metadata.Worker.exe --probe
```

结果：

- Metadata Worker 测试 `20/20` 通过。覆盖 unsafe path/UNC、帧边界/Unicode、当前用户管道、真实 headless worker 进程 client、fake late-bound COM 文档/配置/raw/resolved/drawing reference/broken status、无 key、Windows Property Store/文件基本信息、损坏、权限、超时、取消和协议版本。
- 全解决方案 Release build：`0 warnings / 0 errors`。
- 本机安全 probe：`CapabilityState.Degraded / SWDM_KEY_MISSING`。含义是 DM COM 已注册，但 Credential Manager 目标没有 key；没有尝试用户文件。
- probe 前后现有 `SLDWORKS.exe` process id 集合不变；静态扫描 worker/tests 中 `SldWorks.Application`、注册表写入调用均为零。
- 进程级集成测试退出后没有遗留 `SwSearch.Metadata.Worker` 进程。

### 14.4 当前真实能力与外部边界

| 能力 | 当前结论 |
| --- | --- |
| 文件/管道/超时/降级 | 已由自动测试和 headless 进程集成测试验证 |
| DM class factory | 本机已注册；late-binding probe 可运行 |
| DM license | 未提供，当前明确降级为 `SWDM_KEY_MISSING` |
| 真实 `.SLD*` 完整 DM 提取 | 未验证；不能在无 key/无 fixture 时宣称可用 |
| Windows Property Store | 用临时空扩展名文件验证调用安全；实际 SOLIDWORKS handler 字段覆盖率仍需真实样本确认 |
| SOLIDWORKS 主程序 | 从未由实现或验证启动/附加；架构上禁止 |

要完成真实 DM 兼容验收，需要用户/安装器把合法 key 写入当前用户 Credential Manager 的 `SwSearch/DocumentManagerLicenseKey`（不通过命令行或普通配置传递），并提供受控的 part/assembly/drawing fixture：至少包括中文文件级属性、空属性配置、linked property、多 sheet/view、引用配置、missing/broken model、历史版本/2024 保存版本。测试只读副本，永不修改原设计文件。

### 14.5 已改文件

- `src/SwSearch.Metadata.Worker/**`：worker host/client、协议、安全 key provider、DM/Property Store providers、验证、错误与提取编排。
- `tests/SwSearch.Metadata.Worker.Tests/**`：20 个单元/进程集成测试。
- `src/SwSearch.Contracts/Contracts.cs`：仅追加可选 `ConfigurationNames`。
- `docs/solidworks-metadata-spike.md`：本实现与验证记录。

未修改 Everything、Indexer、主 UI、PropertyViewer、ShellRegistration 或真实注册表；未安装组件，未启动任何 GUI。
