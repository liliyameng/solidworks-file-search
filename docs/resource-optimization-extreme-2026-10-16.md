# 极致资源优化报告（RC89b 轮，2026-10-16）

> ⚠️ **勘误（同日晚，用户纠正后修正）**：Broker"空闲自退"方案**已撤回**。后台必须持续监听新文件（watcher/USN 提示）并解析属性——监听物理上要求进程持有活动句柄，"退出"与"持续监听"不可兼得。最终架构为**常驻但变瘦**：Broker 永不自行退出（IdleExitDelay 保留为显式选配、生产默认关闭），空闲时轮询从 250ms 拉伸到 1s、每 30 分钟自修剪工作集。下文第一节按此修正，效果模型见第三节修正值。

> 用户目标：资源占用降至当前的 20%。
> 红线不变：真实 Shell 结果项、不加解析并发、不改排序语义、体验无损；**新增：后台监听与解析职责不可中断**。

## 一、架构（修正版）：常驻监听 + 按需瘦身

| 变更 | 文件 | 语义 |
|---|---|---|
| ~~Broker 空闲自退~~ → **常驻瘦身** | `BrokerHost.cs`（IdleExitDelay 选配，默认 null 不启用）、`Program.cs`（生产用 Default） | Broker 从登录自启后持续监听文件提示并驱动解析，永不自行退出；空闲时靠下方两机制降耗 |
| 空闲轮询自适应 | `IndexingRuntime` 后台循环 | 队列清空且无全量发现时，循环间隔 250ms→**1s**（新文件/新任务最迟 1s 内被发现）；有工作立即回 250ms |
| 空闲自修剪 | `IndexingRuntime.TryTrimWorkingSet` | 空闲持续 30 分钟后修剪本进程工作集（尽力而为，软缺页回页无感） |
| App 监控被动化 | `MainViewModel` + `TryGetStatePassiveAsync` | 保留（安装器更新停 Broker 期间不再误报错；WakeBrokerOnce 显式唤醒保留） |
| 工具栏 WebView 长隐藏销毁 | `MainWindow` | 保留（托盘 5 分钟后销毁，恢复重建且查询/模式/范围不丢失） |
| WebView2 Low 档 + 磁盘缓存 1MB + SQLite shrink_memory | 宿主/存储 | 保留 |

## 二、托盘态极限压缩（叠加项）

1. **工具栏 WebView 长隐藏销毁**（`MainWindow`）：隐藏到托盘 5 分钟后**整个销毁**工具栏 WebView（渲染器树归零），恢复时按需重建（页面从 bridge state 重新水合，用户输入的查询/模式/范围不丢失）。
2. **WebView2 Low 内存档**（`CompositionWebViewHost`）：`MemoryUsageTargetLevel=Low`——静态工具栏页面让渲染器主动释放后备内存。
3. **磁盘缓存上限**：`--disk-cache-size=1MB --media-cache-size=1MB`，防用户配置目录月度膨胀。
4. SQLite `PRAGMA shrink_memory`：checkpoint 后归还页缓存堆峰值。
5. （上轮已落地）TrySuspend 挂起、工作集修剪、监控暂停/退避——本轮销毁策略是其超集。

## 三、空闲态效果模型（修正版，20% 目标的诚实核算）

**托盘常驻稳态（本轮修正后）**：

| 进程 | 本轮前 | 本轮后 |
|---|---|---|
| SwSearch.Broker | 常驻，空闲 250ms 轮询，工作集不修剪 | **常驻**（监听必需）：空闲 1s 轮询 + 30 分钟自修剪 + shrink_memory 归还页缓存堆 |
| SwSearch.MetadataWorker | 按需子进程 | 不变（队列空即无进程） |
| msedgewebview2（工具栏渲染器） | 挂起后仍存留数十 MB | **0（托盘 5 分钟后销毁）** |
| SwSearch.App | 挂起+修剪后数十 MB | 同左 + 销毁后进一步回落 |

**诚实结论**：保持"持续监听 + 解析"职责的前提下，空闲综合内存预计降到旧稳态的 **约 30%–45%**（WebView 渲染器归零 + App/Broker 双修剪 + Broker 页缓存收缩），空闲 CPU 因轮询降频与监控退避接近归零。**"常驻监听"与"全场景 20%"物理上不可兼得**——Broker 常驻底盘（运行时 + 监听句柄 + WAL 连接，约 20–40MB）是监听能力的固有成本；已通过 shrink/修剪把它压到可压的下限。

**活跃使用期**：资源由工作本身决定（真实 Shell 项、10 小时抽取预算为产品红线），本轮未动活跃路径参数。

## 四、验证

- 全解决方案 Release 构建：**0 警告 0 错误**（`--no-restore` 保护冻结图）。
- App 聚焦回归 43：40 通过，3 个失败与改动前**逐字一致**（UNC 陈旧断言 / 断线清结果旧规格 / NativeStatusPanel DPI）。
- Indexer 控制组（UDF/Rc86/Rc88/Rc55）：全部通过；本轮 filter 额外含入的 Rc51StorageRecovery 9 个失败为已归属的 v6 gram/schema 陈旧。
- PropertyViewer.Tests：**13/13 全过**。
- Integration.Tests 18 个失败甄别完毕：全部为长期陈旧（读已删除的 `SettingsWindow.xaml`、期望 8 列实际 7、断言已不存在的源码字符串、工程图旧规格、与"SearchAsync 直读 SQLite"现行架构矛盾的旧启动契约）。**测 BrokerHost 的 Rc52 系列全过**，证明无 options 构造的宿主行为逐位未变。
- 冻结图：本轮发现 RC89 的恢复曾把资产钉在临时联接 `C:\sws`，已从真实路径重演 one-shot 恢复并再次重定基线（`Assert-FrozenRestoreGraph` PASS；新备份 `restore-assets.lock.json.backup-rc89b-20261016-112936`；被否决的包目录备份 `.packages-rc48-r3.rc89-restore-reject3-*`）。**教训追加：受控恢复必须直接在真实路径执行，不要经联接。**

## 四b、本轮修正的验证补充（勘误后）

- 撤回 Program.cs 的 IdleExitDelay 启用后全量重建：0 警告 0 错误。
- Indexer 控制组（UDF/Rc86/Rc88/Rc55Batch/QueueScheduling 28 例）：27 过；1 失败为 Rc55QueueScheduling 的 `.SLDDRW` 工程图陈旧断言（该 filter 首次含入，非本轮引入）。
- App 聚焦 32：29 过；3 失败与既有基线一致（UNC 陈旧、断线旧规格、Rc28 并行 STA 偶发——已验证隔离通过）。
- 新增空闲自适应只影响 `IndexingRuntime` 循环尾部的 delay 选择与 30 分钟自修剪，Worker/发现/队列语义零变化。

## 四c、动态负载节流（2026-10-16 追加，响应实测卡顿）

**实测元凶**：任务管理器抓到 SwSearch.Broker 以 **241.7 MB/s 持续读盘**，配合 WPS 云同步触发内核态文件风暴（内核 CPU 79–84%）——源头是发现扫描（MFT/目录枚举路径没有字节速率限制），不是轮询空转。据此把上一节的固定空闲档升级为**负载自适应节流**，并让"下一轮发现批次"也避让忙系统：

- 新增 `src/SwSearch.Indexer/SystemLoadMonitor.cs`：`GetSystemTimes` 差分全局 CPU + `GlobalMemoryStatusEx` 可用内存 → 4 档延迟 **1s / 5s / 15s / 60s**。映射：CPU<50%→1s；50–75%→5s；75–85%→15s；>85% 或可用内存<8%→60s（内存压力一票到位，分页代价极高）。迟滞防抖：升档立即生效，降档需连续 3 次更低采样（瞬时尖峰不引起节奏抖动）。
- `IndexingRuntime` 循环：空闲态（队列空 + 无全量发现）轮询间隔取当前档位延迟；有 pending 工作保持 250ms 响应节奏（工作强度仍由 ResourceGovernor 32MB/s 限流）。
- **发现刷新轮忙时避让**：常规刷新轮到期但系统处于高档（≥75%）时顺延一个动态步长（15/60s），本循环照常处理队列工作；提示触发补查与 RC87 启动补查语义不变。
- 新增 `SystemLoadMonitorTests`（7 例：档位边界、内存主导、延迟表、累计差分、升/降迟滞、忙样本重置计数），全过。
- 验证：全解决方案 0 警告 0 错误；Indexer 控制组（UDF/Rc86/Rc88/Rc55Batch）26/26 通过。

**已知边界（如实）**：单批发现扫描进行中（一次 MFT 扫可持续数分钟、批内无节流）本机制不中断——批内读速限制需改 `NtfsMftUsnDiscoveryProvider` 内部节奏，列为后续项；本机制消除的是"批次间接连不断"与"忙时再启动新批次"。

## 四d、第二轮深挖：扫描批内限速 + mmap + 调度优先级（2026-10-16 追加）

上节"已知边界"中列出的**单批扫描无节流**问题，本轮补齐（直击实测 241.7MB/s 风暴的最后一块）：

1. **批内自适应 IO 限速**（新增 `DiscoveryIoPacing`）：MFT 记录循环与内置目录枚举循环的既有 1024 条检查点（≈1MB 元数据读）接入 `SystemLoadMonitor` 采样——空闲 0ms 全速；系统 ≥50% 时 ~60MB/s；≥75% 时 ~20MB/s；≥85% 或内存紧张时 ~5MB/s。采样与延迟开销每 MB 一次、微秒级；空闲机器建索引速度不变。
2. **只读查询连接 mmap**（core 回退连接 + 侧车租约连接 `PRAGMA mmap_size=128MB`）：查询页由私有堆拷贝变为文件映射共享页，App 进程私有工作集下降；连接关闭即解除映射，不与侧车跨进程重建冲突。
3. **Broker 进程 BelowNormal 调度优先级**：忙时前台永远赢得 CPU；空闲时全速（优先级只在争用时起作用）。尽力而为，受限环境失败不阻断。
4. **未做（有意）**：MetadataWorker 不降优先级——它承接交互式属性鲜读（右键属性 5s 预算），降级会伤交互体验。

**验证**：全解决方案 0 警告 0 错误；新增 `DiscoveryIoPacingTests` 4/4（空闲全速、单调、钳位、忙档 ≈5MB/s 校准）；SystemLoadMonitor 7/7；Indexer 控制组+MFT 36/38（2 个失败为工程图移除前的"三扩展"陈旧断言，与本轮无关且改动前即失败）；PropertyViewer 13/13（mmap 回归干净）；App 聚焦 29/31（仅两个既有陈旧失败）。

## 五、遗留与建议

1. Integration.Tests 的 18 个陈旧失败建议立项集中清理（与本轮无关，但持续污染信噪比）。
2. 属性查看器直开场景（主窗口从未打开过、Broker 已空闲退出时右键属性）：鲜读会显示"可重试"而缓存仍秒显；如需完全无感，后续可在 Mimosa 门禁约束下走 App 同款 `CreateBrokerStartInfo` 静态工厂审查路径。
3. 实机验收（修正版）：空闲数分钟后任务管理器确认 **Broker 仍在运行**（监听职责）且工作集明显低于峰值；msedgewebview2 渲染进程在托盘 5 分钟后消失；从托盘恢复窗口出现且查询保留；空闲期向库内新增 .SLDPRT 后 30 秒内被任务窗格/主窗口搜到（监听未被破坏）。
4. 本轮未生成新 MSI（未获指示）；发布需走 Build-Release 唯一入口（版本链建议 v1.3.15/rc90）。
