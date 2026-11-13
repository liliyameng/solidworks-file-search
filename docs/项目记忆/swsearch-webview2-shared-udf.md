---
name: swsearch-webview2-shared-udf
description: SwSearch 两个 WebView2 宿主共用一个用户数据目录，环境参数必须逐字节一致
metadata:
  node_type: memory
  type: project
  originSessionId: session_780b428fa1cf815c4cffba7885d39893
---

`SwSearch.WebUi.CompositionWebViewHost`（主程序工具栏/设置页）与 `SwSearch.SolidWorksAddin.TaskpaneCompositionWebViewHost`（SolidWorks 进程内任务窗格）共用用户数据目录 `%LOCALAPPDATA%\SwSearch\WebView2`，因此共享同一个 WebView2 浏览器进程。

**Why:** 同一用户数据目录下，后来者的 `CoreWebView2Environment.CreateAsync` 若带不同的 `AdditionalBrowserArguments` 会直接失败（COMException，RC90 起主程序带 `--disk-cache-size=1048576 --media-cache-size=1048576` 而旧插件不带，造成"无法加载 Web 界面"）。MSI 升级不能强关 SolidWorks，升级期间新旧 DLL 参数不一致的窗口必然存在。

**How to apply:** 改任一宿主的环境参数时必须同步改另一宿主（两边都有降级重试：带参数失败后用 null options 再试一次以加入既有浏览器进程）。完整异常记录在 `%LOCALAPPDATA%\SwSearch\Logs\app-webui.log` 的 `fault` 行。相关：[[swsearch-build-no-restore-rule]]。
