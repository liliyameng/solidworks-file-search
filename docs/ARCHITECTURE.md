# SwSearch 1.2.0 架构

```text
App / PropertyViewer / SOLIDWORKS Taskpane
                 │ versioned JSON named pipe
                 ▼
        per-user single-instance Broker
          ├─ SQLite schema v4 / FTS5
          ├─ Everything IPC（可选）
          ├─ 内置限速文件发现
          ├─ PropertyConfig provider
          └─ Metadata Worker 子进程
```

Broker 是唯一索引所有者。App 和 PropertyViewer 的发布依赖不含 Indexer、SQLite、SQLitePCLRaw 或 PropertyConfig。SOLIDWORKS Add-in 是 x64 CLR4 WinForms 薄层，只设置回调、创建 Taskpane、显示控件并异步连接 Broker；不在 `SLDWORKS.exe` 中加载 WPF、SQLite、Indexer、Everything 或 Worker。

## 搜索与范围

`SearchQuery` 带版本化 `SearchScope`。文件夹路径被规范化为绝对路径，SQL 同时使用根路径相等和带目录分隔符的 descendant prefix，避免同名前缀越界；不可访问路径不会回退全机。App 单实例激活管道把新的 `--scope-folder` 传给已有窗口。

文件名和已有缓存搜索始终可用。刷新、索引和集成状态分离；后台处理暂停或属性写入因 WAL 压力暂停时不阻塞缓存查询。Everything 只作为发现加速，失败自动回退内置扫描。

## 属性和配置

Metadata Worker 只读解析文件中保存的数据，不依赖 SOLIDWORKS Document Manager API。SQLite schema v4 用 `configurations(file_id, configuration_name, ordinal)` 保存真实配置顺序；无法解析真实名称的 `Config-0` 类索引不作为配置名，也不混入文档属性。

PropertyConfig provider 只读取 `defaults.tsv` 的 `__ConfigFilePath` 一行并按 Base64 UTF-8 解码。活动 cache v2 只有在源 mtime/length 匹配且与 XLSX 规范化结果一致时才使用。优先级为活动设置/已验证缓存/活动 XLSX、last-known-good、本机安装目录备用 XLSX、明确不可用。FileSystemWatcher 使用 750 ms 防抖，另有 30 秒 mtime/length 轮询；新模型完整校验后才原子替换。模型和 UI 均不保存活动完整路径或任何凭据。

## 集成

- SOLIDWORKS：MSI 在 HKLM 注册 Add-in 身份和 x64 CLR4 COM，HKCU AddInsStartup 控制当前用户启停。
- Windows 登录启动：HKCU Run 指向 Broker `--startup --background`。
- Windows 10/旧式文件夹菜单：HKCU 静态目录对象/背景 verb，仅在 build `< 22000`。
- Windows 11 一级菜单目标：原生 x64 `IExplorerCommand` + packaged COM surrogate + signed sparse MSIX；Invoke 只启动 App 的 `--scope-folder` 后立即返回。当前 RC 因工具链和生产签名阻塞而不含 payload。

## 安装与数据

1.2.0 MSI 是 per-machine x64，安装到 ProgramFiles64。旧 1.1.x 是 per-user，跨上下文不能安全原地 MajorUpgrade，因此安装前以 LaunchCondition 明确阻止并要求先卸载旧版。卸载删除程序与本产品注册，不删除 `%LOCALAPPDATA%\SwSearch` 的设置、索引和日志。

自动验证始终通过 `SWSEARCH_DATA_ROOT` 使用隔离目录；不启动产品 GUI、SOLIDWORKS、Everything、Explorer、安装器或 `msiexec`。
