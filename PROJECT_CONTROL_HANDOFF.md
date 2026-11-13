# SOLIDWORKS 文件检索：项目统一交接与总控记录

> **权威状态文档**  
> 本文件汇总本项目从需求提出到 RC88 候选的产品约束、实现选择、已知问题、版本交付、验证证据和后续优先级。后续总控任务必须先阅读本文件；不要依赖某一条超长对话或截图上下文。  
> 最后整理：2026-09-09（RC88 候选，待独立双审）  
> 工作区：\`C:\Users\liliy\Documents\solidworks插件\`

---

## 1. 当前交付状态

| 项目 | 当前值 |
|---|---|
| 当前候选版本 | **v1.3.18 / RC94（门禁通过，未双审、未实机）** |
| 当前安装包 | [SwSearch-x64-v1.3.18-rc94.msi](C:/Users/liliy/Documents/solidworks插件/artifacts/SwSearch-x64-v1.3.18-rc94.msi) |
| 文件大小 | 4,638,607 bytes |
| MSI SHA-256 | `A98C9ECBAF1C1D7EDD1C2406E28CFE32FB7F686304450F72899F112184CBA22B` |
| 验证报告 | [SwSearch-x64-v1.3.18-rc94.verification.txt](C:/Users/liliy/Documents/solidworks插件/artifacts/SwSearch-x64-v1.3.18-rc94.verification.txt) |
| 静态验证 | `SwSearch 1.3.18 RC94 static MSI verification: PASS` |
| 签名/实机状态 | **MSI 未签名；未执行用户侧实机升级；未做独立双审**；静态 PASS 不等于已签名或实机通过 |
| ProductCode | `{DDA7EA84-BDED-4225-8480-9E2A0941D8E6}` |
| UpgradeCode（稳定） | `{D9158356-29B3-44D6-A15A-ACDCE8328703}` |
| 运行时 | .NET 8 **framework-dependent**；安装包不内置 .NET runtime |
| 归档格式 | 不使用 7z |

RC84 虽包含 SOLIDWORKS 2026 \`InternalVersion=19000\` 无 API 属性解析和 extractor v4，但它与 RC83 同为 ProductVersion \`1.3.9\`、文件版本 \`1.3.9.0\`，没有可靠替换旧 DLL；RC85 用真实的 \`1.3.10\` 版本解决了升级载荷问题。RC85 首次运行仍暴露出另一条独立根因：全量文件发现的最终 SQLite 合并与属性提交共用写入门闩，约七分钟的合并会让属性处理停在 0%；同时 extractor v4 的通用升级逻辑会把全部 v3 文件重排，即使其中大量成功缓存与 v4 兼容。RC86 在启动时先让一条待处理属性完成持久化结算，再允许全量发现竞争写入；已有新鲜 ready 快照时不立即重跑全量发现；并在通用重排前一次性、事务化地沿用满足严格条件的 v3 成功缓存。失败、跳过、指纹变化、已有重试/租约或其他不安全记录仍保持 v4 重读，目标 \`0404-139\` 的 \`NOAPI_VERSION_UNSUPPORTED\` 记录不会被误沿用。租约已丢失时也不再误报 worker 恢复。RC86 MSI 已生成并通过静态验证；总控未安装候选、未启动或干扰任何用户软件，仍需用户安装后验证真实大库启动体感和目标属性命中。

RC87 在 RC86 基础上新增启动补查调度：当启动时存在新鲜 ready 快照，首结算完成后，初始发现截止时间取 \`min(正常截止, now+30s)\`，安排 Broker 停止期间新增文件的补查尽快开始。30 秒只约束首次补查的调度截止，不保证扫描完成；暂停、高负载或 SQLite 写锁竞争也可能延迟实际进展。此变更不改善底层 SQLite 合并事务性能或 Everything IPC 查询延迟，也不改变属性抽取或 UI 行为。RC87 还新增 2 个启动补查行为测试，简化了 RC86 版本升级测试，退役了 RC86 ProductCode。RC87 已重新通过唯一 Build-Release 入口及最终静态验证门禁；同样未签名、未实机安装。

RC88 候选在 RC87 基础上收口三项生产变更：运行时锁路径、Discovery 云占位恢复、Queue 冷却/进度/legacy seed 前缀约束；同时 ResourceGovernor read/write 分 gate，保持既有速率与并发限制。RC88 相关 retry 8/8、关键关联 29/29、吞吐专项 5/5、版本测试 3/3 已通过；正式 MSI 已通过静态发布门禁。MiMo + Terra 独立双审已完成，无 P0/P1，可交付；仍未签名、未实盘测速，用户侧实机升级验收待执行。

RC89（v1.3.14）在 RC88 载荷之上合并两类源码变更：①性能与资源专项（原生 Shell 快照 200ms 防抖、AddIDList 快路径填充、真异步填充等待、选中轮询索引探测、Broker 监控隐藏暂停+空闲退避、偏好窗口轻量 state、只读连接 Cache=Private、合并事务分相计时埋点、原生填充性能埋点、任务窗格拒绝计数免分配、删除死代码与空项目）；②资源专项（托盘隐藏时 WebView2 TrySuspend 挂起+恢复、隐藏后本进程工作集修剪）。构建 0 警告；App 聚焦回归 40/43（3 个既有失败：UNC 陈旧断言、断线清结果旧规格、NativeStatusPanel DPI）；Indexer 控制组 22/22；任务窗格行为门禁 40/40；正式 MSI 已通过静态发布门禁。**未做 MiMo/Terra 双审，未实机安装**。
RC91（v1.3.16）在 RC90 之上补齐第二轮资源深挖（见 `docs/resource-optimization-extreme-2026-10-16.md` 第四节d）：①发现扫描**批内**自适应 IO 限速（`DiscoveryIoPacing`——MFT 与目录枚举的 1024 条检查点接入负载采样：空闲 0ms 全速；≥50%/≥75%/≥85% 或内存紧 时约 60/20/5MB/s），彻底闭合实测 241.7MB/s 扫描风暴的批内路径；②只读查询连接 mmap（core 回退 + 侧车租约，私有工作集下降）；③Broker 进程 BelowNormal 调度优先级（忙时让位前台，尽力而为）；④MetadataWorker 有意不降级（交互式属性鲜读 5s 预算）。验证：构建 0w0e；DiscoveryIoPacingTests 4/4、SystemLoadMonitorTests 7/7、Indexer 控制组+MFT 36/38（2 工程图陈旧）、PropertyViewer 13/13、App 聚焦 29/31（2 陈旧）；正式 MSI 通过静态门禁（689 受控文件）。**未双审、未实机**。

RC94（v1.3.18，多语言发布轮；rc93 为同版本中间门禁轮——门禁通过后补记 RC92 实际 ProductCode `C628FD2E` 入退役清单并更新交接头表等受控文件，为使 MSI 与受控清单一致按不可覆盖规则改 rc94 重出，rc93 工件保留为未安装中间产物）按用户需求新增**多语言支持**：设置窗口新增「语言」下拉选项（跟随系统默认 + 简体中文/English/日本語/Deutsch/Français/Español），保存后立即生效、无需重启；系统语言不在列表内回退 English。实现：共享解析器 `src/Shared/UiLanguage.cs`（C#5 兼容，桌面/查看器链接编译、加载项 csc 编译，与 `WindowsScopePath.cs` 同模式——曾放 `SwSearch.WebUi` 下触发查看器"不引用 WebUi"钉死规则，已迁移）；`settings.json` 新键 `language`；安全契约链三处（`WebMessageValidators.PreferencesSave` 白名单 + `DesktopWebBridge.TryCreatePreferencesSaveRequest` + 快照回传）；WebView 层新增 `lang.js` 六语言 65 键表 + `app.js` `t()`/`applyDocumentLanguage()` 即时重渲染 + 六个 html 首帧 `?lang=`；宿主字符串表三张（`AppUiText`/`TaskpaneUserText`/`PropertyViewerText`，zh-CN 与历史字面量逐字节一致）；修复 `JsonAppSettingsStore.PrepareSaveAsync` 重序列化会清掉未知字段的陷阱（列宽防抖保存现保留 language 键，有回归测试）。生效边界：任务窗格语言在下次打开/SW 重启后生效；控制台/日志/注册表 MUIVerb/MSIX 清单保持中文（见 `docs/LOCALIZATION.md`）。测试：新增 `Rc93LanguageTests` 14 项全过；同步更新 5 个钉旧字面量的契约测试（含 Rc53 进度标签）；修复 2 个预存失败（`PreferencesProtocol` 遗留 propertyCard 字段载荷、`DesktopEntry` 单宿主计数——经 git 还原对照证明在 HEAD 已红，与本改动无关）；App 聚焦组 89/91（余 2 失败为 RC89 已记录的 UNC 陈旧断言与断线清结果旧规格）、AppSettings/Coordinator 27/33（6 个失败经还原对照证明在 HEAD 相同，环境/时序性预存）、PropertyViewer 13/13；全解 Release `--no-restore` 0w0e；加载项 csc `/warnaserror+` 通过。附带两处门禁机械化修复：①`Verify-Taskpane.ps1` 的 `GetMethod('TryParse')` 因新增语言重载抛 AmbiguousMatchException，已改为按 3 参数签名显式取原契约重载；②Mimosa PreToolUse 钩子会在 webui 资产目录下落 `.mimosa/hook-state` 污染文件（被 .gitignore 忽略但会被 Publish 打进载荷触发"Unexpected payload extensions"），发布前需清理 src 下所有 `.mimosa`。正式 MSI 通过静态发布门禁（700 受控文件，`SwSearch 1.3.18 RC94 static MSI verification: PASS`，msiSha256=A98C9ECB…）。**未双审、未实机安装**。

RC92（v1.3.17）在 RC91 之上修三类用户实机问题：①**"无法加载 Web 界面"根因**——RC90 给主程序 WebView2 环境加了 `--disk-cache-size=1048576 --media-cache-size=1048576`，但 CLR4 任务窗格插件仍用无参环境；两者共用 `%LOCALAPPDATA%\SwSearch\WebView2`（同一浏览器进程），参数不一致时后创建者 COMException。MSI 升级不能强关 SOLIDWORKS，升级窗口内必然出现新旧参数混跑。修复：插件环境对齐同一参数常量，且两个宿主都加降级重试（带参失败→无参加入既有浏览器进程）；Web 界面故障现在把异常类型/HRESULT/消息完整写入 `app-webui.log` 的 `fault` 行（此前 48px 工具栏裁掉异常详情，只剩 app-settings.log 的类型名）。②**可读取缓存/属性处理永远 99.5x%**——359 个 OneDrive 云占位文件重试耗尽后进 dead_letter 并按 1 小时冷却无限重排，但 `GetMetadataProgressAsync` 的 `WHEN q.reason='CloudPlaceholderCooldown' THEN 0` 把它们硬算 pending；改为按 files 行终态分类（outcome=4 Skipped），与 FILE_NOT_FOUND 终态同语义。已在用户真实 2.3GB 数据库快照验证：83,855/83,855 = 100.00%。这同时让 Broker 队列清空后能进入 SystemLoadMonitor 自适应空闲降档（此前 PendingCount>0 永远全速轮询）。③**用户要求禁用 SW 插件、仅独立软件使用**——已在用户注册表 `HKCU\Software\SolidWorks\AddInsStartup\{F3A7CC42-...}` 置 0（与设置页开关等效，重启 SOLIDWORKS 生效）；设置页开关保留，可随时重新启用。附带：升版机械化修复（AssemblyInfo.cs 1.3.17.0；Publish.ps1/Verify-Msi.ps1 调 Verify-Taskpane 补传 `-BinaryVersion "$Version.0"`，此前只有默认值 1.3.16 恰好匹配才通过）；更新 2 个断言旧串的陈旧测试（Rc46 云终态 Pending→Skipped 语义同步、Rc55 调用图测试的 `_webView?.` 可空形式）。验证：Indexer/WebUi/App Release 0w0e；插件 csc /warnaserror+ 通过；Indexer 聚焦组 45/46（唯一失败为预先存在的工程图陈旧测试，已用还原对照实验证明与本次无关）；App 聚焦组 62/62；任务窗格行为 harness 22/22；正式 MSI 通过静态门禁（689 受控文件）。**未双审、未实机安装**。

RC90（v1.3.15）在 RC89 载荷之上合并资源与节流专项（详见 `docs/resource-optimization-extreme-2026-10-16.md`）：①架构修正——Broker **不**空闲自退（监听新文件与解析要求进程常驻持有句柄；IdleExitDelay 保留为选配、生产默认关闭），改为常驻瘦身：空闲轮询自适应、30 分钟空闲自修剪工作集；②SystemLoadMonitor 负载自适应节流——全局 CPU+可用内存映射 4 档延迟（1/5/15/60s，迟滞防抖），空闲轮询按档运行，常规发现刷新轮在系统忙（≥75%）时顺延一个动态步长（治实测 241.7MB/s 发现读盘风暴引发的内核态卡顿；提示补查与 RC87 启动补查语义不变）；③App 监控被动化+显式唤醒、工具栏 WebView 托盘 5 分钟销毁/恢复重建、WebView2 Low 内存档、磁盘缓存 1MB 上限、SQLite shrink_memory。验证：构建 0w0e；SystemLoadMonitorTests 7/7；Indexer 控制组 26/26；App 聚焦 40/43（3 既有失败不变）；任务窗格行为门禁 40/40（RC89 轮）；正式 MSI 已通过静态发布门禁。**未双审、未实机**。冻结图备注：RC89b 轮曾因经联接恢复把资产钉在 `C:\sws`，已从真实路径重演 one-shot 恢复并重定基线（备份 `restore-assets.lock.json.backup-rc89b-20261016-112936`）；**受控恢复禁止经联接执行**。

**RC89 恢复基线事件（重要）**：发布前普通 `dotnet build` 触发的 RID-less restore 覆盖了 obj/*.assets.json，冻结 RID 图丢失；按 RC84 同款受控离线 one-shot restore 重放恢复（证据 `TestResults/rc89-rid-restore-*`，包目录备份 `.packages-rc48-r3.backup-rc89-20261013-085659`）。因锁定期后 wix/props 输入已合法变更，逐字节复现不可能，已按规范流程以 `New-RestoreAssetsLock.ps1` 重定基线并原子提交新锁（旧锁备份 `installer/restore-assets.lock.json.backup-rc89-20261013-090431`；新锁 receipt 记录于 RC89 发布日志 `frozenRestoreReceipt=DDBF81A820523463E149531D6DDD34956F62E270E95A477068BCC2BF4A75D665`）。**运维教训：本工作区禁止运行不带 `--no-restore` 的普通 `dotnet build/restore`，否则会再次摧毁冻结图**；恢复时非 ASCII 路径会损坏参数编组，需经 ASCII 联接（`New-Item -ItemType Junction C:\sws`）执行。

---

## 2. 不可改变的产品目标

### 2.1 搜索范围与速度

1. 仅支持 SOLIDWORKS 零件和装配体：
   - \`.SLDPRT\`
   - \`.SLDASM\`
2. **不再支持工程图 \`.SLDDRW\`**。这是一项后期的重大范围变更；不要重新加入工程图索引、工程图属性或工程图到模型属性继承逻辑。
3. 不加入 STEP/STP 或其他格式。
4. 搜索必须同时支持：
   - 文件名；
   - 自定义属性；
   - 配置属性；
   - “全部 / 文件名 / 属性”三种逻辑。
5. 用户体验目标为 Everything 式：索引完成后输入即搜、结果秒出；不允许主页固定 200/500/800 条加载上限。
6. 首次文件发现应优先借助 Everything；没有 Everything 时必须退化到内置 NTFS/文件系统发现，不能依赖外部软件才能工作。
7. 后续刷新应只处理新增/修改/删除项，优先利用 Everything 或 NTFS MFT/USN；不要反复完整解析所有文件。
8. 全盘几十万文件的文件名/路径索引目标接近 Everything；属性解析由于文件格式本身更慢，但目标是在不增加解析并发、低资源的前提下尽量压到约 10 小时以内。

### 2.2 资源与稳定性

- 不能明显影响用户正常使用电脑、SOLIDWORKS、桌面或鼠标操作。
- 不允许为了速度无边界提高属性解析并发；此前已明确暂不做 GPU 或高并发解析。
- UI、Broker、Indexer、MetadataWorker 都必须可取消、可恢复，不能让主界面卡死。
- 搜索结果、属性查看优先读缓存；后台再更新，不得让用户等待后台扫描。
- 托盘常驻时才允许后台刷新；托盘“退出”必须真正收口 App、Broker 与自有后台任务，不能残留 \`SwSearch.Broker.exe\`。

### 2.3 安装与安全边界

- 安装/升级应尝试安全关闭本产品自己的 Broker/MetadataWorker；**绝不静默强杀前台 App、属性查看器、SOLIDWORKS 或其他用户程序**。
- 安装可请求管理员权限处理本产品后台组件，但不能通过 PID/name 粗暴杀进程或杀整棵进程树。
- 不使用“USE 检测工具”，不干扰用户正在工作的软件。
- 不内置 .NET 8 运行时；目标机需已有兼容 .NET 8 Desktop Runtime。
- 每次 MSI 版本发布须新 ProductCode、稳定 UpgradeCode，并将上一版 ProductCode 加入退役拒绝列表。

---

## 3. 已确认的功能规格

### 3.1 主界面

- 顶部搜索框：输入后即时检索；文件夹范围搜索必须显示为可取消的范围条件，不可悄悄把路径塞进普通关键字文本且无法取消。
- 顶部模式：**全部 / 文件名 / 属性**，目前采用三个并排按钮，不使用易错、定位异常的下拉框。
- 主界面默认是全盘范围；不能把“文件库”作为主界面固定范围选项。
- 主界面搜索结果必须是原生 Windows Shell 文件项，而不是 HTML/表格文字伪结果，以便支持正常文件图标、Shell 行为及拖放。
- 当前严格显示的真实 Shell 列仅为：

| 显示列 | Shell 属性 | Broker 排序字段 |
|---|---|---|
| 文件名 | \`PKEY_ItemNameDisplay\` | \`fileName\` |
| 文件类型 | \`PKEY_ItemTypeText\` | \`kind\` |
| 所在文件夹 | \`PKEY_ItemFolderPathDisplay\` | \`directory\` |

“命中属性 / 属性值 / 来源”属于查询态信息，原始 \`IShellItem\` 无法真实提供；因此不能再伪装为 Shell 列，否则列名、列宽和内容会错位。

- 点击**列名**排序：升序 → 降序 → 取消排序；第三次回到相关度/默认顺序，并触发 Broker 的完整重新查询，不能只排序当前已加载的一页。
- 不应保留独立的“排序/取消排序”按钮。
- 列宽为比例持久化，默认：
  - 文件名 42%
  - 文件类型 18%
  - 所在文件夹 40%
  用户拖动后保存比例；窗口大小变化时保持比例，而不是固定像素值。
- 底栏始终贴窗口底部，进度文字真居中；不要漂到结果区域中间。
- 底栏只展示三个百分比：**文件、可读取缓存、属性处理**；不要向用户展示“正在整理索引空间”等内部状态。所有用户可见文本应汉化。

### 3.2 原生文件与右键

主界面行必须是原生 Shell 项：

- 双击/打开；
- 在资源管理器中定位；
- 复制完整路径；
- 查看 SOLIDWORKS 属性；
- 可由 Windows/SOLIDWORKS 自身处理拖放到装配体。

不要实现由插件驱动的 SOLIDWORKS 自动拖放或自动建模操作。用户要求“让 SW 来处理拖放”。

右键菜单还包括可选的 Windows 文件/文件夹一级菜单入口：

- “用 SOLIDWORKS 文件检索搜索”；
- “查看 SOLIDWORKS 属性”。

设置中可启用/禁用。Windows 11 一级上下文菜单依赖现代 Context Menu 组件、Windows SDK/MSVC/MakeAppx/SignTool 与签名条件；不能假装所有机器必定可用。

### 3.3 SOLIDWORKS 任务窗格插件

- 可在设置中启用/关闭。
- 强制设计库范围（Library），只能搜 SOLIDWORKS 设计库根，不能在任务窗格任意改成全盘或其他文件夹。
- 仅搜索零件、装配体。
- 任务窗格不显示“来源”和“所在文件夹”列。
- 每个新逻辑搜索重新读取当前 SOLIDWORKS 设计库根；同一结果集分页期间冻结该根集合。
- 任务窗格结果同样应是真实 Shell 文件项，不是单纯列表文字；由 Windows/SOLIDWORKS 处理拖放。
- 所有查询和 IPC 必须后台执行，不能阻塞 SOLIDWORKS UI 或导致“SOLIDWORKS is busy running a command”。
- 首页 Broker 超时上限为 12 秒；分页/健康检查为 2 秒，均可取消。

### 3.4 属性查看与属性卡

- 不打开 SOLIDWORKS 即可通过右键查看属性。
- 默认展示**配置属性**；可切换“配置属性 / 自定义属性”。
- 不再展示“属性卡”模式。
- 读取结果按配置分组，增加行距，便于阅读。
- 删除材料密度、强度等材料物性；只保留材料名称/材质等必要信息。
- 属性查看先显示缓存，再后台刷新到缓存；不能因 Broker 一次超时让窗体长时间空白。
- 用户点击右键查看时，无论文件是否被判定更改，都应把本次读取结果写回缓存。

### 3.5 设置、托盘与配置表

- 设置窗口为独立的 Preferences WebView 页面，不能复用曾经导致卡死/闪退的旧设置代码。
- 设置保留：任务窗格启用、右键菜单启用、开机自启、配置表来源和路径等必要内容。
- 已移除：暂停/恢复后台扫描、文件发现方式、搜索结果列设置（列宽自动记忆）。
- 配置表逻辑：程序启动优先解析并建立内部缓存；解析未完成时使用旧缓存；只在下次重新启动时重新解析。支持当前坚果云设置页面/同目录备用表规则。
- 关闭主窗口只隐藏到托盘；托盘菜单“退出”才是真退出，并且必须退出其 Broker。

---

## 4. 当前架构

| 模块 | 责任 |
|---|---|
| \`SwSearch.App\` | WPF 主程序；WebView2 顶部工具栏 + WinForms ElementHost 原生 Shell 结果 + 原生底栏 |
| \`SwSearch.WebUi\` | WebView2 工具栏、独立设置页面、桥接与安全消息校验 |
| \`SwSearch.Broker\` | 常驻进程；搜索、缓存、状态、属性后台协调 |
| \`SwSearch.Indexer\` | 文件发现、SQLite 核心数据和可重建搜索索引 |
| \`SwSearch.MetadataWorker\` | 低资源属性提取工作者，由 Broker 托管/恢复 |
| \`SwSearch.PropertyViewer\` | 属性查看器 |
| \`SwSearch.SolidWorksAddin\` | SOLIDWORKS 任务窗格、设计库范围搜索、原生 Shell 项承载 |
| \`TaskpaneShellResultsHost\` | 共享 ExplorerBrowser/DefView/IResultsFolder 原生结果宿主 |
| \`installer\` | MSI、升级协调、右键菜单组件、发布/验证脚本 |

### 4.1 原生结果区

当前主界面不再使用网页表格承载结果。它用 \`ExplorerBrowser\`、\`IResultsFolder\`、\`SHCreateItemFromParsingName\` 把真实 \`.SLDPRT/.SLDASM\` 放入 Shell DefView。

RC73 通过共享列契约配置 \`IColumnManager\`，并对设置失败显示可见失败状态，而不是静默展示错位列。Shell 自己的列头被隐藏，以避免 Shell 本地排序只影响已加载页；自定义原生列头调用 Broker 三态排序。

### 4.2 WebView2 与水平滚轮

主 UI 曾按要求改为 WebView2 Composition 宿主；WinForms 原生 Composition 宿主捕获 \`WM_MOUSEHWHEEL\` 并通过 \`CoreWebView2CompositionController.SendMouseInput(HorizontalWheel)\` 转发横向滚轮。

主工具栏目前只负责搜索输入、三个模式按钮和设置入口；不要恢复旧的内嵌设置弹窗或固定定位下拉菜单。

### 4.3 SQLite、缓存与搜索索引

长期目标是把：

- \`core.db\`：文件、属性、配置、任务队列；
- \`search.db\`：FTS、可重建短词/搜索缓存；

分开，避免搜索索引膨胀阻塞属性更新。以前巨量 \`search_short_grams\`、失效属性/配置/工程图引用会使数据库达到约 2.15 GB；策略是：

- 停止/删除巨量短词 gram；
- 3 个字符及以上用 FTS；
- 1–2 字符改为有限 \`search_documents\` 扫描，不再新增爆炸式 gram；
- 每批约 1,000–2,000 行清理失效数据；
- 以实际页占用而非物理文件长度判断可复用空间；
- 搜索索引可随时重建，不能拖住核心缓存和属性写入。

---

## 5. 文件发现、百分比与缓存口径

### 5.1 正确的发现流程

1. 首次发现：Everything 优先；若不可用则内置发现。
2. 后续发现：基于 Everything 或 NTFS/MFT/USN，仅识别变化文件。
3. 扫描完成后对比当前发现快照与缓存：
   - 新增/修改文件加入属性更新队列；
   - 已删除文件标为失效；
   - 不应让百分比在每一条文件被重新发现时持续倒退。
4. 属性处理优先装配体、零件；工程图已取消。
5. 只有成功提交完整快照时，正式活动文件集才切换，避免发现半途失败导致部分数据混入正式库。

### 5.2 三个进度的含义

| 文本 | 正确含义 |
|---|---|
| 文件 | 当前发现轮的可信进度；只有已提交完整快照（generation>0、last_success 非空、source_state=ready）才能显示 100% |
| 可读取缓存 | 当前活动文件中可用于快速搜索/查看的缓存覆盖率 |
| 属性处理 | 当前活动文件中已完成、部分完成、终态跳过或终态失败的处理进度；若要表达“真正成功提取属性”，应另行明确，不能混淆 |

对于 Running/Paused/Stalled 的未提交 live 发现：有可信分母时可以显示实时比例，但即使分子等于估计总数也最多 **99.99%**；无分母时显示 \`--%\`，不能拿旧缓存伪造 100%。

“可读取缓存”小于“属性处理”可能发生，因为终态跳过/失败可以被算作处理结算，但不一定具有完整可读属性。未来若继续引发误解，应明确展示“已处理”和“完整属性缓存”两个不同概念。

### 5.3 历史发现失败（同事电脑的关键证据）

同事机器曾出现 1,940/1,957 条总数、百分比 80%或100%但实际已发现数万条的现象。根因不是文件实际只有这些，而是：

- 本轮使用 \`FallbackDiscoveryProvider\`，没有成功走 Everything IPC；
- 临时发现区已到 15,000、50,000、60,000+ 候选（C/D/E 均有结果）；
- 约 14 分 35 秒后出现 \`DISCOVERY_QUERY_FAILED\`；
- 临时发现未提交、被清空，正式活动库仍是 generation 0 的旧缓存；
- 旧 1,961 条里删除了 21 个已取消支持的 \`.SLDDRW\`，因此显示 1,940。

以后诊断必须区分：

- \`discovery_generation\`
- \`last_success_utc\`
- \`source_state\`
- \`discovery_stage\`
- active files / staging files

不得仅凭“队列为 0”或旧缓存状态显示文件 100%。应记录非敏感诊断：provider、origin、mode、kinds、roots_count、roots_hash、term_length、term_hash、result_count、library_rejected_count、latency、error_code；**禁止记录原始路径和原始搜索词**。

---

## 6. 已修复的关键问题与版本时间线

| 版本/阶段 | 重要完成项 | 已知后续问题 |
|---|---|---|
| 早期 v1.1–v1.2 | 初版全盘 SOLIDWORKS 搜索、属性缓存、Everything 可选加速、安装器、右键和任务窗格 | 旧 WinForms/UI、500 条限制、属性服务不稳定、安装阻塞、设置闪退 |
| RC13 安装安全审计 | 收紧安装协调：仅 Broker/MetadataWorker 可强制终止；同一原生句柄身份验证；不杀整棵树；全局安装互斥、watchdog、路径/reparse/短名门禁 | 过程复杂，不能因安全门禁跳过而发布 |
| v1.2.x | WebView2 UI、托盘、独立设置、文件夹范围、属性缓存、设计库/右键、部分性能改造 | 多次出现 Broker 断开、属性进度停滞、假列表/拖放不真实 |
| v1.3.0 | **取消工程图支持**；移除零件/装配体复选框；开始简化范围 | 某次布局使列表区消失、进度条漂中间 |
| RC70 / 1.3.4 | 原生 Explorer/Shell 真文件结果、独立 Settings、分页与稳定查询签名 | 列标题与真实 Shell 内容不对应；独立排序按钮不符合用户期望 |
| RC71 / 1.3.5 | Loaded 后初始化 Shell；文件进度不再把 partial/stale 显示成 100%；任务窗格 FTS Library 过滤在 LIMIT 前；任务窗格首屏超时调整 | 百分比运行中仍可能缺估计分母；模式控件同步问题 |
| RC72 / 1.3.6 | 顶部改为全部/文件名/属性三按钮；修复 \`maintoolbar\` ready 白名单；完成正式冻结恢复/lock 重基线 | 需要继续解决真实 Shell 列头和比例布局 |
| RC73 / 1.3.7 | 三个真实 Shell 列、列头三态排序、删除独立排序按钮；真实 IColumnManager 读回门禁通过 | 窗口 resize/状态刷新仍较重 |
| RC74 / 1.3.8 | 底栏真居中；列宽比例 42/18/40；resize/status 轮询降载；相同文本/相同 Rect/无变化列不重复写 | 引入 NativeStatusPanel 构造期 NRE，导致“显示界面未能完成初始化” |
| **RC75 / 1.3.9** | 修 NativeStatusPanel 生命周期：先创建控件、SuspendLayout、\`_layoutReady\` 门闩后布局，避免构造期 OnLayout 空引用 | 待用户最基础安装/启动验证；大型 Shell 结果的体感性能仍需观察 |
| **RC83 / 1.3.9** | 1–2 字非空查询改走现有侧车 LIKE；core UDF 仅解析最终获胜属性行；Everything IPC 关闭先排空查询；任务窗格监控丢弃关闭后的迟到回调 | 待用户安装/启动与大库短词体感验证；单字符侧车 LIKE 仍是有界产品取舍 |
| **RC84 / 1.3.9** | 候选载荷支持并通过实文件验证 SOLIDWORKS 2026 \`InternalVersion=19000\` 属性解析；extractor v4 自动重排旧零件/装配体；受控离线恢复并重基线冻结 RID 图；构建/验证锁定官方 SOLIDWORKS 2026 PIA | 与 RC83 同版本，安装时没有替换旧 DLL，所以上述新载荷未在用户运行时生效；完整 Worker 套件仍有 5 个既有/环境性失败需后续收敛 |
| **RC85 / 1.3.10** | 修复 RC84 与 RC83 同版本导致 MSI 未替换旧载荷：提升 ProductVersion/FileVersion、新 ProductCode、RC84 退役，并增加候选版本必须高于最近已发布版本的构建/验证门禁 | 已通过正式静态发布门禁与载荷核验，尚未由用户实际升级安装；安装后需等待 extractor v4 一次性重处理，再验证 \`0404-139\` 命中 |
| **RC86 / 1.3.11** | 修复启动时全量发现写锁让属性提交长期停在 0%；新鲜 ready 快照不再立即全量刷新；先完成一条属性持久化结算；严格选择性沿用兼容的 v3 成功缓存并让不安全记录继续由 v4 重读；租约竞争不再误报 worker 恢复 | 已通过聚焦回归、Release 编译与正式静态发布门禁；尚未由用户实际升级安装，需验证大库启动后属性进度能尽快增长、\`0404-139\` 被 v4 重读并命中 |
| **RC87 / 1.3.12** | 启动补查调度：fresh ready 快照在首结算完成后将初始发现截止设为 \`min(正常截止, now+30s)\`，安排 Broker 停止期间新增文件的补查；退役 RC86 ProductCode；新增 2 个补查行为测试 | 已通过构建、Release 编译、静态验证和行为测试门禁（14 索引 + 3 版本 + 40 任务窗格行为）；未签名、未实机安装；30s 仅为首次补查调度时限，不保证扫描完成 |

---

## 7. 历史故障根因速查

### 7.1 设置窗口打不开/卡死

旧设置实现多次抛 \`InvalidOperationException\` 或偏好保存失败。现已改为独立 \`WebUiView.Preferences\` 窗口与 \`preferences.html\`，不要回退复用旧设置代码。

### 7.2 主界面白屏、列表区消失、进度条在中间

原生 Shell \`ExplorerBrowser\` 曾在 MainWindow 构造阶段创建，导致 dispatcher/layout 生命周期异常。当前正确顺序：

1. WPF 窗体 Loaded；
2. \`Dispatcher.Yield(ContextIdle)\`；
3. 幂等挂载 ElementHost / ExplorerBrowser；
4. 挂载失败时底栏显示中文失败文本和“重试文件视图”。

XAML 主行必须保持：工具栏 \`48\` / 结果区 \`*\` / 状态栏 \`28\`。状态栏是独立底部行。

### 7.3 RC74 启动“显示界面未能完成初始化”

日志证据：\`app-startup.log\` 在 2026-08-10 记录 \`bootstrap|System.NullReferenceException\`。根因是 \`NativeStatusPanel\` 构造器设置 Dock/BackColor/Padding 时同步触发 \`OnLayout\`，但标签/重试按钮尚未初始化，访问 \`ProgressLabel\` 触发空引用。

RC75 修复在：

- \`src/SwSearch.App/NativeStatusPanel.cs\`

策略：\`SuspendLayout\` → 创建全部子控件 → \`_layoutReady\` → 设置布局属性 → \`ResumeLayout(true)\`。聚焦构造/首次布局回归已通过。

### 7.4 任务窗格能搜文件名但搜不到物料属性

根因是 FTS 先全盘取 \`LIMIT 8000\` 候选，再做设计库/文件夹过滤。高频物料编码的前 8000 条可能全在库外，库内真实命中被截掉。

RC71 已把 kind、系统目录排除、Folder、Library roots、excluded IDs 过滤推入 FTS candidate CTE 的 LIMIT 前，并保持后置防御过滤。回归覆盖：8,050 个库外同属性候选后仍应找到库内零件/装配体。

### 7.5 属性服务“未运行”、处理率长期停滞

历史上 MetadataWorker 生命周期脆弱、Broker 断线/重启、慢属性提取和数据库索引膨胀共同造成。当前方向是**由常驻 Broker 托管属性处理**，属性查看读缓存优先，后台 worker 可恢复。后续必须继续检查：

- Broker 托管 worker 是否真的存活；
- 每轮队列、claimed/extracted/committed 是否增长；
- 搜索索引清理是否暂停属性写入；
- UI 不要把终态跳过误称为“完整属性”。

RC86 对“文件 100% 后属性处理长时间 0%”补充了实证根因和窄修复：RC85 日志中完整发现约耗时 428 秒，属性提交等待约 423 秒，两者重叠且由发现最终合并长期占用 SQLite 写入门闩。RC86 的启动屏障只等待一条真实属性任务完成持久化结算；没有任务、暂停、服务不可用或超时都会有界放行，不会把发现永久挂住。新鲜且已提交的 ready 快照按刷新周期复用，但文件系统提示仍可提前触发刷新。RC87 将此"按刷新周期"改为在首结算完成后取 \`min(刷新周期, now+30s)\` 作为启动首次发现截止，以安排发现停止期间新增文件的补查；这只改变发现启动调度，不碰属性抽取或 UI。extractor v3→v4 兼容缓存沿用使用一次性维护标记，必须先于通用升级重排执行，避免第二次启动把失败、修改或重试记录误识别成可沿用缓存。

### 7.6 安装慢、安装无法关闭旧程序

旧安装器会卡在验证、FilesInUse 或 TOKEN_CONTEXT_INVALID，曾出现 MSI 2613。正确策略是安全协调本产品后台组件、等待/重试、绝不干扰 SOLIDWORKS。若旧主程序正在占用 DLL，用户从托盘“退出”后再升级是最后手段；不要建议结束任何无关进程。

### 7.7 “在文件夹中定位”定位错误

这是仍须重点回归的功能：必须对当前 Shell 选中的实际绝对解析路径调用资源管理器定位，不能错误使用搜索范围、文件库根或截断路径。它不应修改搜索栏内容，也不应打开无关文件夹。

---

## 8. 当前性能策略与尚未完成的重点

### 8.1 已完成的低风险性能优化（RC74）

- 状态轮询从 1 秒改为 2 秒；
- 主窗口隐藏或最小化时暂停轮询，恢复时立即刷新；
- 状态文本未变化时不重复写入；
- Shell \`SetRect\` 相同则跳过；
- Shell 列布局初次 \`SetColumns\`，之后只对变化列 \`SetColumnInfo\`；
- 列宽拖动/窗口 resize 合并、节流、无变化不提交；
- 关闭窗口时先快照最新列比例，再有界保存；
- 搜索模式按钮无重复点击查询。

### 8.2 仍需实测/后续实现

大型结果集的原生 Shell 填充仍是最可能的卡顿热点：

- 每个结果要 \`SHCreateItemFromParsingName\` + \`IResultsFolder.AddItem\`；
- 当前按页、顺序地填充；大量结果时会有 O(N) Shell COM/DefView 更新；
- 不能为了速度退回网页假列表，也不能丢掉真实拖放能力。

下一步应优先采用**不增加解析并发**的窄优化：

1. 输入搜索增加短、可取消的 debounce，避免被新关键词覆盖的完整 Shell 填充启动；
2. Shell 填充在小批次后有界让出，取消旧查询；
3. 仅在必要时继续加载后续页；
4. 对大结果集测量首屏可见时间、输入到首个结果时间、UI 帧卡顿；
5. 不改变 Broker 全量排序/索引语义。

---

## 9. 当前待办清单（按优先级）

### P1：下一轮安装后先验证

1. 安装 RC87，确认它升级替换 RC86 并能正常打开；首次启动观察属性处理是否在短时间内开始增长，以及 Broker 停止期间新增的文件是否在 30s 补查窗口内开始被发现。
2. 底栏文本完整、居中，窗口 resize 后仍不被裁剪。
3. 三列真实文件名/文件类型/所在文件夹与行内容一一对应。
4. 拖动列分隔线、重启、改变窗口大小后比例保持。
5. 点击文件名/文件类型/所在文件夹列名三次，排序依次为升序、降序、取消。
6. 主界面与 SOLIDWORKS 任务窗格按同一物料编码搜索：
   - 主界面 All 命中；
   - 目标在设计库根内时任务窗格 Library 命中；
   - 目标不在设计库根内时任务窗格合理为空。
7. 搜索 \`0404-139\`，确认 \`D:\\拓创自建库\\04管接头\\0404等径接头\\双公接头\\公接头-HF4.SLDPRT\` 经 v4 重新提取后命中；若仍未命中，应保留数据库与日志证据继续诊断，不能把进度百分比当作属性成功率。

### P1：仍需修复/核验

1. **Everything 优先发现与同事电脑总数**：确保 Everything IPC 成功时真的成为顶层 provider；若失败要中文说明且不能让 staging 成功发现的数据整体回滚为旧缓存。
2. **文件夹定位**：必须定位当前文件，不能打开无关文件夹或把路径写进搜索框。
3. **托盘退出**：退出后 Broker 不残留。
4. **属性后台处理**：RC86 已修复启动写锁等待、兼容缓存全量重排和租约误报；仍需实机确认 Broker 托管 worker、首条结算、后续批量提交及异常恢复都持续增长。
5. **大型查询卡顿**：保留真实 Shell 文件项目的前提下改善首屏和输入响应。

### P2：体验与诊断

- 原生 Shell 缩略图/图标与 SOLIDWORKS 设计库缩略图不一致；
- Windows 11 一级右键菜单在未满足签名/SDK前要显示明确中文可用性，不要无响应；
- \`library_rejected_count\` 应在客户端二次根过滤后记录，避免诊断值失真；
- 统一所有英文错误文案；
- “可读取缓存 / 属性处理”说明应减少用户对 100%/非100%含义的误解。

---

## 10. 发布、恢复与验证规程

### 10.1 唯一发布入口

只允许在源码稳定后从仓库根运行：

\`\`\`powershell
& .\\installer\\Build-Release.ps1 -UseControlledFrozenRestoreGraphReceipt
\`\`\`

不要直接调用 \`Build-Msi.ps1\`，不要绕过发布/验证链。

### 10.2 恢复基线

- \`installer\\restore-assets.lock.json\` 是当前正式冻结恢复基线；RC84 因普通测试覆盖 RID asset，重新走 Freeze→独立审批→Execute 的离线 one-shot restore、全图断言和候选 lock 原子提交。当前 lock SHA-256 为 \`6FA9941387B771D00D47E324D7200584CFA43782724650A0926BE0242622F4ED\`。
- RC84 恢复证据位于 \`TestResults\\rc48-r3-oneshot-restore-20260828-203027007\`；恢复前包目录保留为 \`.packages-rc48-r3.backup-rc84-20260828-202907551\`，旧 lock 保留为 \`installer\\restore-assets.lock.json.backup-rc84-20260828-203027007\`。不要在没有明确清理授权时删除这些回退证据。
- 不要随意删除、重建或手工改 \`.packages-rc48-r3\`、offline feed、restore lock。
- 如果 RID asset 再次缺失，应先审计目标框架/RID，再执行受控离线恢复，不允许网络 restore 或随意复制历史 \`obj\`。

### 10.3 基础发布验证要求

用户明确希望避免无意义的大型验证。每版至少保留：

1. 受影响项目 Release 编译；
2. 受影响功能的聚焦测试；
3. MSI 静态验证；
4. 不在打包/验证期间启动、关闭、枚举或干扰用户软件；
5. 不安装候选包，除非用户明确授权实机安装。

---

## 11. 版本与文件定位

### 11.1 关键源码

- [MainWindow.xaml.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.App/MainWindow.xaml.cs)
- [NativeStatusPanel.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.App/NativeStatusPanel.cs)
- [NativeShellResultsColumnHeader.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.App/NativeShellResultsColumnHeader.cs)
- [NativeShellResultsHost.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.WebUi/Native/NativeShellResultsHost.cs)
- [TaskpaneShellResultsHost.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.SolidWorksAddin/TaskpaneShellResultsHost.cs)
- [TaskpaneControl.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.SolidWorksAddin/TaskpaneControl.cs)
- [SqliteIndexStore.Search.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.Indexer/SqliteIndexStore.Search.cs)
- [BrokerProtocol.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.SolidWorksAddin/BrokerProtocol.cs)
- [WebMessageValidators.cs](C:/Users/liliy/Documents/solidworks插件/src/SwSearch.WebUi/Security/WebMessageValidators.cs)
- [Directory.Build.props](C:/Users/liliy/Documents/solidworks插件/Directory.Build.props)

### 11.2 关键测试

- [Rc71ScopedFtsCandidateTests.cs](C:/Users/liliy/Documents/solidworks插件/tests/SwSearch.Indexer.Tests/Rc71ScopedFtsCandidateTests.cs)
- [Rc73NativeColumnContractTests.cs](C:/Users/liliy/Documents/solidworks插件/tests/SwSearch.App.Tests/Rc73NativeColumnContractTests.cs)
- [Rc74ColumnRatioAndStatusTests.cs](C:/Users/liliy/Documents/solidworks插件/tests/SwSearch.App.Tests/Rc74ColumnRatioAndStatusTests.cs)
- [Rc86MetadataStartupTests.cs](C:/Users/liliy/Documents/solidworks插件/tests/SwSearch.Indexer.Tests/Rc86MetadataStartupTests.cs)
- [Rc86VersionedUpgradeTests.cs](C:/Users/liliy/Documents/solidworks插件/tests/SwSearch.InstallerCoordinator.Tests/Rc86VersionedUpgradeTests.cs)

### 11.3 运行诊断位置

- \`%LOCALAPPDATA%\\SwSearch\\Logs\\app-startup.log\`
- \`%LOCALAPPDATA%\\SwSearch\\Logs\\app-webui.log\`
- \`%LOCALAPPDATA%\\SwSearch\\Logs\\broker-connection.log\`
- \`%LOCALAPPDATA%\\SwSearch\\Logs\\SolidWorksAddin.log\`
- \`%LOCALAPPDATA%\\SwSearch\\settings.json\`
- SQLite/状态数据库位于用户 SwSearch 本地缓存目录；处理前先复制/只读审计，禁止贸然删除 2GB 索引。

---

## 12. 协作与总控规则

1. 总控负责拆分、审阅、交付和与用户对接；实现应优先委派给独立子任务。
2. 不要沿用历史对话中对某个模型的临时偏好；尤其不要把 DeepSeek 固定为默认执行者或审核者，模型选择必须服从当前全局路由规则。
3. 不要置顶任务。
4. 用户希望子任务能在独立对话中可见；若平台可创建用户线程，应为重大独立工作创建可见线程，否则如实说明内部协作限制。
5. 不要因为上下文很长而反复重新探索已在本文件明确的决定。
6. 任何新改动先检查本文件的不可变边界、当前版本、未解决项，再决定是否实施。
7. 模型协作、额度路由与交付审核以当前全局 \`AGENTS.md\` 为准；不要固定或反复使用 DeepSeek。源码、测试、文档或发布脚本发生实质修改时，按全局规则完成独立复核、本地测试和正式发布门禁。

---

## 13. 一句话交接结论

项目已从“网页列表式 SOLIDWORKS 文件搜索器”演进为“缓存/索引驱动、Everything 优先且内置发现仅作兜底、Broker 托管属性、主界面和任务窗格均使用真实 Shell 文件项的 SOLIDWORKS 零件/装配体检索器”。RC87 历史交付保持不变；RC88 已通过聚焦回归、Release 编译、最终 Build-Release、正式静态发布门禁及 MiMo + Terra 独立双审，无 P0/P1，可交付。候选未签名、未安装、未实盘测速；用户侧实机升级验收待执行。P2 历史注释遗留已记录于 `TestResults/rc88-release/final-review.md`，不影响运行。
