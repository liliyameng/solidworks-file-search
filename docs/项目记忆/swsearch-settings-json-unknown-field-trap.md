---
name: swsearch-settings-json-unknown-field-trap
description: JsonAppSettingsStore 整体重序列化会丢 settings.json 未知字段；新设置键必须同时进
  AppSettings+SettingsDocument 或走保留逻辑
metadata:
  node_type: memory
  type: project
  originSessionId: sess_2bbff512-ddda-4bf6-b99b-87203a505800
---

`SwSearch.App/JsonAppSettingsStore.PrepareSaveAsync`（AppSettings.cs）把 settings.json **整体反序列化→重序列化**：不在 `SettingsDocument` 里的键会被静默丢弃。`UserPreferencesRepository.WritePropertyCardPreferences` 则是就地编辑 JsonObject、保留未知键——两个写入方行为不一致。

**Why:** settings.json 有两个独立写入方（偏好保存 vs 列宽/布局防抖保存）。给设置新增键（如 RC93 的 `language`）只改 Repository 不够：用户改完语言后任何一次列宽调整都会触发 JsonAppSettingsStore 重写文件，把语言键清掉，表现为"语言设置随机丢失"。RC93 用 `AppSettings.Language` 参与往返 + `PreservePersistedLanguage`（自身 Language 为空时把已持久化值拼回）双保险修复。

**How to apply:** 以后给 settings.json 加新键时：(1) 键加进 `AppSettings` record 和 `SettingsDocument`；(2) 若该键归偏好面板所有、App 设置快照不带它，则模仿 `PreservePersistedLanguage` 在 `PrepareSaveAsync` 里回拼；(3) 加一条"经 JsonAppSettingsStore 重存后键仍存活"的回归测试（见 `tests/SwSearch.App.Tests/Rc93LanguageTests.JsonAppSettingsStore_ColumnSave_PreservesPersistedLanguage`）。相关：[[swsearch-build-no-restore-rule]]。
