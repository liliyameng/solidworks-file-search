---
name: git-push-after-every-change
description: 每次修改后必须提交并推送 GitHub 仓库，项目记忆与统一文档也要同步推送
metadata:
  node_type: memory
  type: feedback
  originSessionId: sess_2bbff512-ddda-4bf6-b99b-87203a505800
---

用户要求（2026-11-13）：本项目（SOLIDWORKS 文件检索）的每一次修改都必须提交并推送到 GitHub 公共仓库 `liliyameng/solidworks-file-search`（原请求名"solidworks文件检索"，GitHub 仓库名不接受中文，经用户选定此英文名）。项目记忆快照与统一文档更新后同样要同步推送。

**Why:** 公共仓库是用户认可的权威发布渠道，记忆与文档只存在本地会造成本地/远程漂移；用户明确要求记忆与统一文档随代码一起保持同步。

**How to apply:** 每次完成代码或文档修改后立即 `git add` → `git commit` → `git push`（origin/main）。若 ZCode 项目记忆目录 `C:\Users\liliy\.zcode\cli\memories\projects\solidworks-5a01c5b3ce6f3e78\memory\` 有新增或更新（含 MEMORY.md 索引），先把整个目录的最新内容复制到仓库 `docs/项目记忆/`，再一并提交推送。统一文档指 `docs/`、README.md、PROJECT_CONTROL_HANDOFF.md。相关：[[swsearch-build-no-restore-rule]]、[[swsearch-webview2-shared-udf]]。
