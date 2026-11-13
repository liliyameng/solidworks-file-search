---
name: swsearch-build-no-restore-rule
description: SwSearch 工作区禁止不带 --no-restore 的 dotnet build/restore（冻结恢复图保护）
metadata:
  node_type: memory
  type: project
  originSessionId: session_780b428fa1cf815c4cffba7885d39893
---

在 `C:\Users\liliy\Documents\solidworks插件` 工作区，任何 `dotnet build` / `dotnet test` 都必须带 `--no-restore`。

**Why:** RC89 曾因普通 `dotnet build` 触发 RID-less restore 覆盖 obj/*.assets.json，摧毁了 `installer/restore-assets.lock.json` 冻结恢复图，需要走受控离线 one-shot restore 重放恢复（经 ASCII 联接 `C:\sws` 执行，非 ASCII 路径会损坏参数编队）。

**How to apply:** 开发迭代用 `dotnet build <csproj> -c Release --no-restore`；测试先 build 测试工程再 `dotnet test --no-restore --no-build`。发布只允许 `installer\Build-Release.ps1 -UseControlledFrozenRestoreGraphReceipt`（禁止直接调 Build-Msi.ps1）。插件 DLL 不在 sln 里，由 `installer/Build-Taskpane.ps1` 用 .NET Framework 4.x csc.exe（C# 5 语法！）编译，catch 内不能 await。相关：[[swsearch-webview2-shared-udf]]。
