# BlockGame v0.1

BlockGame 是一个面向 Windows 11 的自我约束工具。它通过后台守护程序监控进程启动，匹配规则后立即终止程序，并用管理密码、解除冷静期和一次性卸载令牌增加绕过阻力。

## 当前版本包含

- 文件名规则：例如 `steam.exe`、`game*.exe`
- 完整路径规则：支持 `*` 和 `?`
- 规则添加、启用、停用和删除
- 文件名可直接填写 `qq` 或 `qq*`，程序会自动补全为 `qq.exe` / `qq*.exe`
- 单条文件名规则也可填写 `qq;wechat;steam*`，每一项分别匹配
- 首次运行会添加“常见游戏平台”和“常见影音平台”两条内置预设，默认停用，可勾选启用、右键修改或删除
- 最高优先级“调试一键复位”：不受密码、锁定和冷静期限制，立即暂停拦截、解锁并清空规则
- Windows 系统托盘常驻图标；关闭主窗口后可从托盘重新打开、复位或退出控制面板
- 守护服务异常退出或被任务管理器结束后，按 0.5 秒、1 秒、3 秒三档快速恢复；服务管理器中的普通“停止”也会视为非授权终止并自动拉起，连续稳定运行 24 小时后重置失败计数
- 启动或从托盘重新打开管理界面时验证一次管理密码，进入界面后不再对每项操作重复询问
- 规则首列提供启用复选框，可连续点击多条规则快速启用或停用
- 成功拦截会实时写入“拦截事件”日志；守护服务会直接向当前 Windows 登录桌面发送“XX 软件已被拦截”提示，托盘控制面板作为失败兜底
- LocalSystem Windows 服务，可自动重启
- PBKDF2-SHA256 管理密码哈希和输错限速
- 解除保护确认文本和冷静期（1 分钟～30 天）
- 一次性、十分钟有效的卸载授权令牌
- 本地 JSON 配置、JSONL 审计日志和守护心跳
- Windows 关键进程安全名单，避免规则误伤系统

## 构建

本机已经验证 .NET 9 SDK。默认构建为 framework-dependent 发布：目标电脑需要安装 .NET 9 Desktop Runtime。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

如果希望发布包自带运行时（体积更大）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -SelfContained
```

## 安装

必须在“管理员 PowerShell”中执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

安装后打开“BlockGame 游戏自律助手”：

1. 设置管理密码和解除冷静期。
2. 添加要禁止的 `.exe` 文件名或完整路径。
3. 点击“启用并锁定保护”。
4. 后台服务会持续监控进程。

如果直接运行 `artifacts\publish\app\BlockGame.App.exe`，程序在首次进入主界面前也会自动查找并安装 `BlockGameGuard` 服务；它会从同级的 `..\guard` 目录复制守护程序到 `C:\Program Files\BlockGame`，然后启动服务。若只复制了 App 文件而没有携带 guard 发布目录，自动安装会失败并给出提示。

## 正常卸载

先在应用的“设置”页生成卸载授权，然后在 Windows“已安装的应用”中执行卸载。授权十分钟有效且只能使用一次。若保护处于锁定状态，必须先提交解除申请并等待冷静期结束。

软件更新、重新安装和授权卸载会写入一个最多有效 30 秒的一次性维护标记，因此这些操作可以正常停止守护服务，不会触发自动拉起。

## 开发测试

```powershell
dotnet run --project .\tests\BlockGame.SelfTest\BlockGame.SelfTest.csproj
```

也可以使用 `scripts\run-guard-console.ps1` 在控制台运行守护程序。开发模式的数据目录为项目下的 `.dev-data`。

## 重要限制

v0.1 使用高频进程监控，因此被拦截程序可能短暂出现；AppLocker / WDAC 的创建和签名策略将在后续版本加入。拥有本机管理员权限的人仍可以进入安全模式、离线修改文件或重装系统。本项目不使用隐藏持久化、内核驱动或破坏系统的方式。
