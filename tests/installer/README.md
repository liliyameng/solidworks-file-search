# MSI 隔离生命周期验证（安全入口）

本目录用于在两个临时本地标准账号中验证 SwSearch MSI。`Start-IsolatedMsiLifecycleValidation.ps1` 是唯一总控入口；`Invoke-MsiLifecycleValidation.ps1` 只接受总控在受保护 staging 中原子生成的 schema-v2 manifest 和随机 nonce，禁止从源码路径直接加 `-Execute`。

固定输入：

- 当前 MSI 1.1.1：`418CB6AD9CDC4B78ECDF8F83B7AFEF2E323E7E2E233F3BCD18C7FC4983465DFA`
- 旧 MSI 1.0.1：`55B6F988249B65822FFF7E683334BD5533722AA7FE5B1A843B9F616C81AAFE77`
- lifecycle 源：`9C2A40382BC677DE48759577AFA131D2F6326DE46FBED97B3CF0B232B09E456C`
- helper 源：`3638AEE0B3551155166E8DDEC27EE76A557AFF75C19E82FE3220820FFEE91719`
- 总控 wrapper：`BFE89C4A2C1471FA31649BC1C05B62B1F1059F5FA9C08DB6ADDDC64D535473C9`
- 受保护用户 SID：`S-1-5-21-6737918-3018883656-3777255787-1001`

## 只读预检

直接运行 wrapper 且不传 `-Execute` 只做身份、固定 hash、MSI identity、Installer 空闲、进程和旧版状态检查。它不创建目录、账号、profile、ACL 或 helper，也不调用 `msiexec`：

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\Users\liliy\Documents\solidworks插件\tests\installer\Start-IsolatedMsiLifecycleValidation.ps1"
```

## 唯一真实执行命令

必须在“以管理员身份运行”的 64 位 Windows PowerShell 5.1 中完整粘贴下面一个命令块。它只读取一次固定源码 bytes，先核对内嵌 SHA256，再把同一份 bytes 写入 `%ProgramData%` 的管理员保护目录、复核副本 hash，最后从该副本执行。不要直接在源码路径加 `-Execute`。

```powershell
$src='C:\Users\liliy\Documents\solidworks插件\tests\installer\Start-IsolatedMsiLifecycleValidation.ps1';$expected='BFE89C4A2C1471FA31649BC1C05B62B1F1059F5FA9C08DB6ADDDC64D535473C9';$bytes=[IO.File]::ReadAllBytes($src);$sha=[Security.Cryptography.SHA256]::Create();try{$actual=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()};if($actual-ne$expected){throw "拒绝执行：wrapper SHA256 不匹配：$actual"};$base=Join-Path $env:ProgramData 'SwSearch';[IO.Directory]::CreateDirectory($base)|Out-Null;if(((Get-Item -LiteralPath $base -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw '拒绝执行：ProgramData\SwSearch 是 reparse/junction'};$acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetAccessRuleProtection($true,$false);$inh=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit';$prop=[Security.AccessControl.PropagationFlags]::None;$allow=[Security.AccessControl.AccessControlType]::Allow;foreach($sidText in @('S-1-5-18','S-1-5-32-544','S-1-5-21-6737918-3018883656-3777255787-1001')){$sid=New-Object Security.Principal.SecurityIdentifier($sidText);$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,[Security.AccessControl.FileSystemRights]::FullControl,$inh,$prop,$allow)))|Out-Null};$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));Set-Acl -LiteralPath $base -AclObject $acl;$boot=Join-Path $base 'ValidationBootstrap';[IO.Directory]::CreateDirectory($boot)|Out-Null;if(((Get-Item -LiteralPath $boot -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw '拒绝执行：bootstrap 是 reparse/junction'};Set-Acl -LiteralPath $boot -AclObject $acl;$copyRoot=Join-Path $boot $expected;[IO.Directory]::CreateDirectory($copyRoot)|Out-Null;if(((Get-Item -LiteralPath $copyRoot -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw '拒绝执行：hash 目录是 reparse/junction'};Set-Acl -LiteralPath $copyRoot -AclObject $acl;$dst=Join-Path $copyRoot 'Start-IsolatedMsiLifecycleValidation.ps1';[IO.File]::WriteAllBytes($dst,$bytes);if((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash-ne$expected){throw '拒绝执行：受保护副本复核失败'};& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File $dst -Execute -ConfirmIsolatedSystemChanges -Confirm:$false
```

本威胁模型阻止普通测试 SID、低权限进程以及路径/JSON 伪造；已控制本机管理员或 SYSTEM 的攻击者可绕过本地 ACL 与脚本，这不在本验证器的防护目标内。

## 身份、manifest 与结果契约

- 子进程从 `WindowsIdentity` 取得真实 SID，经 SID Translate、`Get-LocalUser -SID` 和 `Win32_UserProfile` 反查 SAM/profile；不信任 `$env:USERNAME` 或调用者自报身份。
- 保护 SID 永久禁止成为测试 SID。manifest 固定 RunId、nonce、SID、SAM、profile、scenario、保护 SID、MSI identity、源码和 staged hash。
- 测试 SID 对 staging/manifest 只有读取执行权限；每个结果目录只有对应的一个非特权 SID 可写。
- child JSON 使用严格 schema/type/identity 检查；根字段缺失或多余、伪 boolean、nonce/SID/SAM/profile/MSI 不符、required check 缺失/重复/false 都是 `FAIL`。
- A、B、C1、C2 必须全部 `PASS`；D 才允许 `SKIP`。任何 `MANUAL_CLEANUP` 永远使 summary 为 `FAIL`。

## 场景与 C2 等值证明

- A：1.1.1 干净安装、payload/ARP/右键/CLI、卸载和无残留。
- B：1.0.1 → 1.1.1 主升级、动作顺序和无残留。
- C1：helper 响应 `WM_CLOSE`，升级必须成功。
- C2：helper 忽略 `WM_CLOSE`，升级必须在 25 秒内明确拒绝。拒绝前后比较完整旧版状态：文件/目录清单、安装目录 ACL、ARP/version/ProductState/RelatedProducts、三扩展 context verb、开始菜单、Installer/UserData 及相关 registry；并证明新版未注册。helper 的存活与清理都绑定 PID、creation、path、command line、SID、session。

## 持久 journal 与崩溃恢复

真实执行在受保护 bootstrap 目录生成 `<RunId>.journal.json`。每个账号/profile/ACL/staging/场景/产品阶段/helper/controller/结果变更前后都以临时文件 + `WriteThrough` + `Flush(true)` 原子更新；不记录密码。

恢复只接受该受保护目录中的严格 journal，并再次要求管理员令牌和显式确认。Installer 正在运行、controller 仍存活、PID identity 不符、SID/profile 不精确、UserData 残留或任何 reparse/junction 时一律拒绝自动清理。先重新执行上面的固定-bytes bootstrap 步骤以得到受保护 `$dst`，然后运行：

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File $dst -RecoverFromJournal "C:\ProgramData\SwSearch\ValidationBootstrap\BFE89C4A2C1471FA31649BC1C05B62B1F1059F5FA9C08DB6ADDDC64D535473C9\<RunId>.journal.json" -ConfirmIsolatedSystemChanges -Confirm:$false
```

profile 只允许精确的 `C:\Users\SwMsiTest_*`，删除前拒绝 profile 根或子项 reparse/junction；删除后复核 CIM、ProfileList、HKU、Installer UserData、账号、实际目录和临时 SID ACL 全部不存在。

## 静态与安全 fixtures

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\Users\liliy\Documents\solidworks插件\tests\installer\Test-MsiLifecycleSecurityFixtures.ps1"
```

fixtures 仅使用内存和系统临时目录，不创建账号、不调用 MSI、不打开 GUI，覆盖伪 SID、伪 JSON、篡改 staged script/helper、profile junction、非法 journal recovery 以及 C2 registry 差异。
