# Windows 11 一级文件夹菜单构建与生命周期

生产方案固定为 x64 原生 `IExplorerCommand`、packaged COM `com:SurrogateServer`（dllhost 隔离）和 external-location sparse MSIX。`Directory` 与 `Directory\Background` 同时声明；静态 HKCU verb 仅用于 Windows 10/旧式菜单回退。

`Invoke` 只读取目标目录并从 DLL 自身目录启动 `SwSearch.App.exe --scope-folder "<绝对路径>"`，立即返回。原生组件不得链接或加载 WPF、SQLite、Indexer、Everything、Broker 或 Metadata Worker。

生产构建需要获准的 MSVC x64 与 Windows 11 SDK 环境（cl/link、C++/WinRT、MakeAppx、SignTool）以及不进入仓库/安装包的生产 PFX。清单 Publisher 必须与证书 Subject 完全一致。外层 MSI 签名不能替代内部 MSIX 签名；自签证书仅允许开发验证。

MSI 生命周期设计：安装时把原生 DLL、注册助手和已签名 MSIX放入 Program Files，并 stage/provision；当前用户设置开关由注册助手调用 `AddPackageByUriAsync`（设置 ExternalLocationUri）或 `RemovePackageAsync`。变更后调用 `SHChangeNotify`，提示可能需要重启 Explorer 或注销；禁止强杀 Explorer。卸载最终移除本产品 package identity、packaged COM 注册和 payload。跨 v1.1.x per-user 到 v1.2.0 per-machine 由 MSI 明确阻止并提示先卸旧版，用户 LocalAppData 索引/设置保留。

当前工作机缺上述原生工具链与生产签名材料，`Build-ModernContextMenu.ps1` 会在预检阶段失败且不生成伪 payload；因此 RC 不得宣称 Windows 11 一级菜单完成。
