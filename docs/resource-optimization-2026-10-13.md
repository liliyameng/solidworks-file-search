# 资源占用优化报告（RC88 后续，2026-10-13）

> 目标：在**不改变任何可感知行为**的前提下降低常驻资源占用（内存 / CPU / 后台 IO）。
> 红线（沿用 PROJECT_CONTROL_HANDOFF）：真实 Shell 结果项、不加属性解析并发、不改 Broker 排序语义、托盘退出零残留。
> 本文覆盖“资源专项”轮次；性能专项（防抖 / AddIDList / 合并计时等）见代码注释与上轮验证记录。

## 一、本轮改动（2 个代码点）

### 1. 托盘隐藏时挂起 WebView2 渲染器内存
- `src/SwSearch.WebUi/CompositionWebViewHost.cs`：新增 `SuspendForHiddenWindow()` / `ResumeFromHiddenWindow()`。
  - 依赖 SDK 1.0.4078.44 已内置的 `CoreWebView2.TrySuspendAsync()` / `Resume()` / `IsSuspended`（已用反射对缓存包验证签名）。
  - `SuspendInput()` 本就把 `controller.IsVisible=false`；挂起在隐藏动作完成后经 `BeginInvoke` 在宿主 UI 线程执行。若控制器仍可见，`TrySuspendAsync` 只返回 false——快速隐藏/恢复循环不可能挂起活动界面。
- `src/SwSearch.App/MainWindow.xaml.cs`：`HideToTray` 末尾挂起；`RestoreFromTray` 与取消最小化的可见分支恢复（幂等，未挂起时为空操作）。
- 效果：托盘常驻（本产品最长的生命周期状态）期间，msedgewebview2.exe 渲染器释放后备内存；恢复时由运行时自动换页，无可感知延迟。

### 2. 隐藏到托盘后修剪本进程工作集
- `MainWindow.ScheduleWorkingSetTrim()`：`SetProcessWorkingSetSize(伪句柄, -1, -1)`，`ApplicationIdle` 优先级、仅本进程、尽力而为（失败吞掉）。
- 恢复时的软缺页换页在窗口不可见期间完成，用户无感。不触碰任何其他进程（符合“绝不干扰用户程序”边界）。

## 二、复核后**有意不做**的点（含原因）

| 候选 | 结论 | 原因 |
|---|---|---|
| 状态轮询（可见时 2s）空闲退避 | 不做 | 退避会让“空闲→活动”的进度出现最多数秒延迟，属可感知变化，违反本轮红线；隐藏场景已由上轮监控暂停覆盖 |
| Broker `IdleDelay` 250ms 放宽 | 不做 | 空闲唤醒 4 次/秒的 CPU 占比可忽略（<0.1%），而它参与文件提示批处理时延，收益/风险比差 |
| ServerGC / GC 配置 | 不做 | Workstation+并发已是 .NET 8 默认；ServerGC 明显增内存，与目标相反 |
| 修剪 msedgewebview2 子进程工作集 | 不做 | 需枚举并操作他人进程，违背产品对进程操作的自我约束；WebView2 挂起已覆盖该内存 |
| SQLite 只读连接 `cache_size` 调小 | 不做 | 大库 FTS 查询依赖页缓存，调小直接伤首屏时延（体验红线） |

## 三、叠加的既有资源收益（上轮已落地并验证）

- 原生 Shell 快照 200ms 防抖 + 隐藏不排程：消除连续输入的整页 RemoveAll/重灌 COM 风暴（CPU/IO）。
- `AddIDList` 快路径：大结果集填充从“每文件完整 IShellItem 绑定”降为“PIDL 解析 + 添加”（CPU）。
- 偏好窗口只收轻量 `{preferences}` 载荷：省去每轮最多 500 条结果 DTO 的二次序列化（CPU/内存）。
- Broker 监控隐藏暂停 + 空闲退避（20s→最长 60s，随基线缩放）：托盘期零监控管道流量。
- 填充等待真异步化：每页少占一个被阻塞的线程池线程。
- 选中轮询改索引探测：焦点静止时从“每 500ms 全链 COM 解析”降为“单次 GetFocusedItem 调用”。

## 四、验证

- `dotnet build SolidWorksSearch.sln -c Release`：0 警告 0 错误（TreatWarningsAsErrors 生效）。
- App 聚焦回归（MainViewModel/Rc28/Rc52/Rc70/Rc74，共 43）：40 通过；3 个失败均为**既有问题**（陈旧断言 ×2：UNC 状态串与断线清结果均系工程图时代旧规格；NativeStatusPanel 450/449 为 DPI 环境性），与本轮改动无关，且失败集与改动前完全一致。
- 任务窗格行为门禁 40/40（上轮，本轮未触碰 addin 代码）。
- 挂起/恢复为运行时行为，静态测试无法覆盖；**实机验收步骤见第六节**。

## 五、预期收益与测量

预期（量级，以实机为准）：
- 托盘常驻：App 进程工作集下降（典型数十 MB，取决于常驻托管集）；msedgewebview2.exe 渲染器内存显著回落（挂起语义即为此设计，典型 30–100MB 量级）。
- 可见使用期：无变化（本轮不触碰可见路径的任何参数）。

测量方法：运行 `docs/measure-footprint.ps1`（只读采样，输出 CSV）：
```powershell
powershell -ExecutionPolicy Bypass -File .\docs\measure-footprint.ps1 -Seconds 300
```
建议序列：可见 2 分钟 → 点 X 隐藏到托盘 5 分钟 → 恢复 1 分钟。对比隐藏段前后 App 与 msedgewebview2 的工作集/私有内存曲线。

注意：`msedgewebview2` 计数是**全机口径**（其他应用也使用该运行时），请用“隐藏前后同一时段的差值”判断本产品效果；`phase` 列由 App 主窗口句柄自动推导，无需手工标注。

## 六、实机验收清单（安装含本改动的版本后）

1. 搜索、排序、右键、拖放、任务窗格全部行为不变（本轮零可见路径变更）。
2. 隐藏到托盘数分钟后：任务管理器中 SwSearch.App 与 msedgewebview2（其子进程）内存明显低于隐藏前；CPU 近 0。
3. 从托盘恢复：窗口即点即显、搜索即时响应（挂起恢复为后台换页，无白屏/卡顿）。
4. 反复快速隐藏/恢复 10 次：无白屏、无输入失灵（验证快速循环下挂起不生效于可见表面）。

## 七、文件清单

| 文件 | 改动 |
|---|---|
| `src/SwSearch.WebUi/CompositionWebViewHost.cs` | +`SuspendForHiddenWindow` / `ResumeFromHiddenWindow` / `TrySuspendCoreAsync` |
| `src/SwSearch.App/MainWindow.xaml.cs` | HideToTray 挂起+修剪；RestoreFromTray/可见分支恢复；`SetProcessWorkingSetSize` P/Invoke |
| `docs/measure-footprint.ps1` | 新增：只读资源采样脚本 |

发布提醒：正式安装包仍须走 `installer\Build-Release.ps1 -UseControlledFrozenRestoreGraphReceipt` 唯一入口与静态门禁；本轮未生成 MSI。
