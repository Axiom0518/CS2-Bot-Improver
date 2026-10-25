# Windows 打包与部署

## 2026-10-25 启动闪退修复

游戏为 `1.41.9.2`，安装的 CounterStrikeSharp 是 `1.0.376`。Windows 在 17:40:18
和 20:10:01 记录了 `counterstrikesharp.dll + 0x7e354` 的 `0xc0000005` 访问冲突；
原生日志在 `CGameSystem::BuildGameSessionManifest` 后中断。游戏目录的 BotAI 还在使用
旧补丁字节，只有 `39/40` 项成功。

[CounterStrikeSharp 1.0.378](https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.378)
明确修复游戏 `1.41.9.2` 的兼容性，包括 SDK 和原生接口更新。因此修复需要同时更新
原生框架、API 和 gamedata，仅替换 C# 插件 DLL 不足以解决该问题。

本项目将框架 gamedata 更新到官方 1.0.378，保留本地 `CheckTransmit` 项；
部署工具固定并校验以下 Windows 发行包：CounterStrikeSharp 1.0.378、Metamod
2.0.0-dev+1483、BotHider 0.6.0、BotController 0.7.2、BotVision 0.3.1。
Metamod 同步升级，满足新版原生 Bot 插件要求的 build 1481 及以上。

9 个本地插件和 2 个共享 API 重新编译，BotState 有原有的 23 个警告，所有项目均为
0 错误。部署后 239 个包内文件全部通过 SHA256 核对，其中 127 个文件发生变化，覆盖前
已备份。`InventorySimulator`、游戏的 `gameinfo.gi`、现有框架 core 配置和 .NET 运行时
均不在该覆盖包内。

用 `-insecure` 启动 `de_dust2` 离线地图测试超过 90 秒，进程响应正常，没有新的 Windows
崩溃事件；日志确认所有插件完成加载，BotAI `42/42` 项补丁生效。该验证覆盖启动及地图加载，
不代表完成了长时间、多地图或 Linux 游戏测试。

## 使用

先退出 CS2，在项目根目录执行：

```powershell
python scripts/package_windows.py
```

如需同时部署到本机游戏：

```powershell
python scripts/package_windows.py --deploy "D:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo"
```

需要 Python 3.9+、Git 和 .NET 10 SDK。下载使用系统代理设置，官方发行包的版本、地址和
SHA256 固定在 `deployment/windows-runtime.json`。工具只部署配置、数据和构建产物，
不会把 C# 源文件或项目文件复制到游戏插件目录。投掷物数据同时映射到本分支实际读取的
`plugins/NadeSystem/grenades`。

每次产生独立的 `artifacts/windows-时间戳/`，包括 ZIP、逐文件哈希清单、构建日志、
`backup/` 和部署记录 `deployment.json`。备份全部完成后才开始覆盖；部署时再次确认
游戏已关闭，并逐文件核验写入结果。未出现在包内的游戏文件不会删除。
