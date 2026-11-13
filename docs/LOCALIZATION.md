# 多语言（语言下拉选项）实现说明 — RC93 / v1.3.18

设置窗口新增「语言 / Language」下拉选项：**跟随系统**（默认）+ 简体中文、English、日本語、Deutsch、Français、Español 六种内置语言。保存后**立即生效、无需重启**；系统语言不在列表内时回退 English。

## 语言集与解析

| 配置值 | 含义 |
|---|---|
| `auto`（默认） | 跟随 Windows 显示语言：`zh*` → `zh-CN`，`en/de/fr/ja/es` 直配，其他回退 `en` |
| `zh-CN` / `en` / `ja` / `de` / `fr` / `es` | 固定语言 |

- 共享解析器：`src/Shared/UiLanguage.cs`（命名空间 `SwSearch.WebUi`）。**C# 5 兼容**——该文件同时被桌面程序（net8 链接编译）、属性查看器（链接编译）与 SOLIDWORKS 加载项（`installer/Build-Taskpane.ps1` 用 .NET Framework 4 csc.exe 编译）三方编译，禁用 C#6+ 语法（字符串插值、`?.`、表达式体成员、字典索引初始化器）。
- 解析语义：`ResolveEffective(configured)` — 非法配置值按 `auto` 处理；`auto` 读 `CultureInfo.CurrentUICulture` 前缀映射；未命中回退 `UiLanguage.Fallback`（`en`）。

## 持久化与生效链路

1. **存储**：`%LOCALAPPDATA%\SwSearch\settings.json` 新键 `"language"`（`SWSEARCH_DATA_ROOT` 隔离规则同其他键）。
2. **写入路径**（设置窗口保存）：
   `app.js savePreferences()` → `preferences.save` 消息（新增 `language` 字段，经 `WebMessageValidators.PreferencesSave` 白名单校验，仅接受 7 个合法值）→ `DesktopWebBridge.TryCreatePreferencesSaveRequest` → `UserPreferencesRepository.SaveAsync`（原子写，未知字段保留）。
3. **生效（App 进程，立即）**：保存成功后 `MainViewModel.UpdateLanguageInMemory` 更新 `UiLanguageEffective` → `DesktopWebBridge.PublishState()` 向工具栏 WebView 推送带 `language` 字段的 state → `app.js` 检测 language 变化即重渲染当前 shell（`applyDocumentLanguage` 同步 `<html lang>` 与标题），无需 reload。托盘菜单在每次展开时按当前语言重建；原生状态栏重试按钮、设置窗口标题同步更新。
4. **首帧语言**：`WebViewHostOptions.Language` → `WebViewOrigin.EntryPoint(view, language)` 生成 `https://swsearch.local/{view}.html?lang=xx`，页面在首条 state 到达前即以正确语言渲染。
5. **属性查看器**：每次启动经 `PropertyViewerText.ResolveConfiguredLanguage()` 读 settings.json；窗口标题/标签/表格列头/状态文本全部走 `PropertyViewerText` 表。WPF 列头不在视觉树内，由 `NativePropertyWindow` 构造函数显式赋值。
6. **SOLIDWORKS 任务窗格**：`TaskpaneControl` 构造时读一次语言（`TaskpaneUserText.ReadConfiguredLanguage`），状态文本走 `TaskpaneUserText`（C#5 词典），WebView 导航带 `?lang=`，state 推送带 `language`。**限制**：任务窗格控件存活期内不跨进程推送语言变更——下次打开任务窗格或重启 SOLIDWORKS 后生效。
7. **JsonAppSettingsStore 陷阱（已修）**：`JsonAppSettingsStore.PrepareSaveAsync` 会整体重序列化 settings.json，丢弃未知字段。列宽防抖保存若不处理会**清掉 language 键**。修复：`AppSettings.Language` 参与往返；`PrepareSaveAsync` 在自身 Language 为空时调用 `PreservePersistedLanguage` 把已持久化值拼回。回归测试：`Rc93LanguageTests.JsonAppSettingsStore_ColumnSave_PreservesPersistedLanguage`。

## 字符串表位置

| 层 | 文件 | 说明 |
|---|---|---|
| WebView（6 个表面共用） | `src/SwSearch.WebUi/Assets/webui/lang.js` | `window.SW_STRINGS`：六语言各 65 键（键集必须一致，有契约测试）；`SW_T`/`SW_FORMAT` 运行时取词；`SW_LANG_OPTIONS` 下拉选项（语言名用本名，不翻译） |
| 桌面宿主 | `src/SwSearch.App/AppUiText.cs` | 状态/摘要/结果标签/偏好状态/托盘/窗口标题（~150 键 × 6） |
| 任务窗格 | `src/SwSearch.SolidWorksAddin/TaskpaneUserText.cs` | C#5 词典语法 |
| 属性查看器 | `src/SwSearch.PropertyViewer/PropertyViewerText.cs` | |

**zh-CN 值与历史字面量逐字节一致**（中文系统默认观感与旧版完全相同）。

## 不翻译的范围（刻意）

- 控制台输出、日志、诊断（`app-startup.log`、`app-webui.log` 等）保持中文（开发/排障层）。
- 注册表 `MUIVerb`（文件夹右键"使用 SOLIDWORKS 文件检索此文件夹"）、MSIX `AppxManifest` DisplayName、`RegistrationPlan.DisplayName` —— OS 层注册一次，改语言不重注册。
- 领域数据：属性名匹配表（`材料名称` 等）、`propertyDisplayOrder` 默认示例——这些是与 SOLIDWORKS 数据对应的业务标识，不是 UI 文案。
- Broker/Indexer/Metadata Worker 的服务端消息：客户端 `NormalizeCapabilityMessage` 已把已知码翻译为当前语言；Broker 端透传的中文 `ChineseMessage`（版本不匹配详情等）按原文显示。

## 新增一种语言的步骤

1. `src/Shared/UiLanguage.cs`：`ConfiguredValues` 与 `FromTwoLetter` 加前缀映射。
2. `lang.js`：复制 zh-CN 键集翻译；`SW_LANG_OPTIONS` 加本名选项。键集一致性由 `Rc93LanguageTests.LangJs_AllLanguageTablesExposeIdenticalKeySets` 强制。
3. `AppUiText.cs` / `TaskpaneUserText.cs` / `PropertyViewerText.cs`：各加一张词典。
4. `WebMessageValidators.PreferencesSave` 无需改（值合法性走 `UiLanguage.IsValidConfiguredValue`）。

## 测试与验证（RC93）

- 新增 `tests/SwSearch.App.Tests/Rc93LanguageTests.cs`：14 项——解析矩阵、持久化往返、列宽保存保留 language、消息契约接受/拒绝、lang.js 六 html 引用顺序、六表键集一致、app.js 发送/回显/即时切换。
- 同步更新 4 个钉旧字面量的陈旧契约测试（改指 t() 形式）；另修复 2 个**预存失败**（`PreferencesProtocol...` 遗留 propertyCard 字段、`DesktopEntry...` 单宿主计数——均经 git 还原对照证明在 HEAD 已失败，与本改动无关）。
- 构建：`dotnet build` 全解 `--no-restore` 0 警告 0 错误（TreatWarningsAsErrors）；加载项 `Build-Taskpane.ps1` csc `/warnaserror+` 通过；`UiLanguage.cs` 移入 `src/Shared/`（与 `WindowsScopePath.cs` 同模式）以满足属性查看器"不引用 SwSearch.WebUi"的钉死架构规则。
