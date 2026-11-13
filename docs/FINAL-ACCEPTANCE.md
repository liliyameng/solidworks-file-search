# SOLIDWORKS 文件检索 1.2.0 RC16：候选静态验收边界

日期：2026-07-17

## 候选工件

- MSI：`artifacts/SwSearch-x64-v1.2.0-rc16.msi`
- 自包含发布目录：`artifacts/SwSearch-win-x64/`
- MSI 哈希清单：`artifacts/SwSearch-x64-v1.2.0-rc16.sha256.txt`
- 只读验证报告：`artifacts/SwSearch-x64-v1.2.0-rc16.verification.txt`
- 受控源码清单：`artifacts/controlled-source-v1.2.0-rc16.sha256.txt`
- 安装动作 Binary 哈希清单：`artifacts/installer-actions-v1.2.0-rc16.sha256.json`

RC1 至 RC15 均已退役；RC15 的 MSI 保持原字节不变。RC16 使用新的 ProductCode，并保持稳定 UpgradeCode；同版本 major upgrade 必须迁移 RC15 的旧 COM 注册。本文列出 RC16 构建时必须通过的验收项；在实际候选及报告生成前不表示已通过。

## 候选必须自动覆盖

- Broker 单实例、同目录隐藏拉起、命名管道协议、有界连接/响应超时、断线重试。
- 缓存先显示，后台发现/属性更新不阻塞 Pipe 或 SQLite WAL 读查询。
- 80 ms 输入即搜、回车立即、IME 组合半成品抑制、空查询、过滤器/目录即时重查、query generation 取消和乱序响应隔离；正常流程没有搜索按钮前置条件。
- App/Taskpane “全电脑/文件夹”范围只做纯语法规范化；离线 UNC 与不存在但语法有效的绝对路径仍查询严格目录边界内的历史缓存，绝不回退全电脑。空、相对或语法非法路径不发请求。
- 实际 CAB App `OnStartup`/窗口构造调用图不含同步设置/文件/目录 I/O 或阻塞等待；UI 先用内存默认值显示，设置仅由有界后台代次读取并通过 Dispatcher 回填，用户设置/列/范围修改、窗口关闭和新代次均阻止旧快照覆盖。实际 CAB CLR4 Taskpane 构造和路径提交链仍不含同步设置/目录 I/O；断开 UNC 构造/提交在 50 ms 内返回。
- App、Contracts、CLR4 Taskpane 与 SQLite 对 Windows 范围路径使用同一黄金矩阵；COM/LPT 后的 ASCII 1–9 及上标 ¹/²/³ 被精确拒绝，其他上标/全角/组合与兼容 Unicode 代码点不被改写。
- 搜索候选使用单消费者有界去重队列；真实共享写门被占用时，非空关键词缓存命中仍在 500 ms 内返回。
- 等价 ActiveSearch 入队为 `changes()=0`；低优先级提权和失败重试可改变调度，但绝不增加搜索 revision；当前 extractor 版本且完整的元数据不再入队。
- 相同查询在消费者释放内存去重键后重复至少 10 次不会形成 revision 反馈环；真实属性提交只发布一次 revision，客户端只合并刷新一次。
- 主数据库长度严格读取；主库/目录缺失报告 `INDEX_DATABASE_MISSING`。仅 WAL checkpoint 消失竞态返回 0，Unauthorized 和其他 IOException 不吞并。
- 仅初始化连接使用 `ReadWriteCreate`；所有 post-init 读写连接强制 `ReadWrite`。确定性检查—打开 TOCTOU 门禁验证主库在探测后被移走时不会重建空数据库。
- Everything 缺失透明回退、真实属性 327 路径只读脱敏门禁、FileName/目录边界、SQLite schema v4。
- App/Viewer/Taskpane 薄客户端依赖边界；Taskpane x64 CLR4/AMD64 COM ABI 与编译后命名管道/协调器行为 harness 20/20。
- MSI per-machine/ProgramFiles64、固定 UpgradeCode、新 ProductCode、payload/CAB 逐路径哈希一致。
- MSI immediate impersonated bridge 从同一个当前线程 impersonation token 取得 individual account SID 与 `TokenSessionId`，用 `BCryptGenRandom` 生成 nonce，并生成一次安装会话共享的单调绝对截止时间。SID/session/nonce 只进入隐藏私有属性与严格 CAD，不进入日志。
- user-ipc 从自身 token 复核 SID/session，Broker 与 Worker 在服务端复核自身 SID/session；两端使用 session-specific、`CurrentUserOnly` 管道，并要求同一连接上的 nonce-bound 随机 challenge 后才接受关闭。Broker/Worker 共用完全相同的 SID/session/nonce，错误 session、错误 challenge、任意替换 nonce 和固定旧 pipe 均 fail-closed。deferred/no-impersonate 阶段硬校验 LocalSystem，只走最终路径和同句柄 strict-handle 协调，且严格早于文件替换和旧产品移除。
- App/PropertyViewer 永不进入强制终止 allowlist，交由 Restart Manager/FilesInUse 提示和重试；Broker/Worker 不再使用按进程名 CloseApplication，也不再使用固定四组件 Type 19 失败文案。
- Coordinator 与 x64 `/MT` native MSI bridge 仅嵌入 `Binary` 表，不进入 `File`/CAB/publish payload；WatchdogHelper、NativeHelper、tests 和 PDB 同样为零。
- 候选流水线全程 `--no-restore`；精确冻结每个受控项目的 `project.assets.json`/dgspec、SDK/reference-pack 标识、唯一 workspace packageFolders 与依赖图。任何 HTTP(S) source、downloadDependencies、用户全局包缓存、缺失/变化资产或 restore/force-evaluate 绕行均在构建前 fail-closed。
- 最终 MSI `Binary` 表必须是精确集合；Coordinator/Bridge 从 MSI 流提取到唯一检查目录并锁定哈希。Bridge 必须为 AMD64，精确导出 `CaptureTrustedInstallerContext` 且 imports 在固定系统依赖白名单内；提取 Coordinator `--self-test` 与 Bridge 无效 handle gate 只在严格超时的隔离进程运行，前后哈希不变。
- 固定 RC12 基线：57,687,391 bytes；SHA256 `4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5`；ProductCode `{49D3B604-E741-4D2A-975B-D1E84C0A8528}`；UpgradeCode `{D9158356-29B3-44D6-A15A-ACDCE8328703}`；Version `1.2.0`；`ALLUSERS=1`；File 表 493 行。验证前/后/最终都复算字节和身份，sidecar 只有在另有历史基线时才声明历史不变。
- 候选生成后受控源码、测试、安装脚本、验证脚本与项目文件 SHA256/文件集合不变，修改时间晚于候选的文件数为 0。

所有自动化测试使用隔离临时目录；未读取或写入真实 `%LOCALAPPDATA%\SwSearch`。静态验收不启动产品 GUI、SOLIDWORKS、Everything、Explorer、安装器或 `msiexec`。

## 明确未完成

- 未生成或签名 Windows 11 sparse MSIX，不能声称现代一级右键菜单已生产交付。
- 未进行生产 Authenticode 签名。
- 未执行真实安装、升级、卸载或实机验证。
- 未在 SOLIDWORKS 2024 进程内验证 Taskpane；当前仅有官方 ABI 基线和无界面 CLR4 行为测试。
- RC16 已完成候选前的 no-restore 编译与静态/隔离门禁，但尚未构建 MSI。必须在独立预审放行后由唯一 `Build-Release.ps1` 入口构建并验证；本文不声称真实安装或 SOLIDWORKS 进程内验证已完成。
