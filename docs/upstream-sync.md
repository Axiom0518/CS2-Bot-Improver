# 上游同步记录

2026-10-25 将 `ed0ard/CS2-Bot-Improver` 的 `main` 合并到本分支，主仓库基准为
[`c51d783`](https://github.com/ed0ard/CS2-Bot-Improver/commit/c51d783)。
同步前的本地工作保存在提交 `981321e` 和本地分支 `codex/pre-upstream-sync-20261025`。

本仓库继续直接管理插件文件。上游子模块对应的配置及 C# 代码按下列固定提交同步，
没有将本地插件目录替换成子模块。三个原生插件目录继续保存配置；C++ 源码和原生二进制
仍由各自上游仓库及发行包提供，本次没有构建或安装原生二进制。

| 组件 | 上游仓库 | 固定提交 |
| --- | --- | --- |
| BotController | XBribo/CS2-Bot-Controller | `e53785147e1a7c8e7de1174539526f0ea1912a3c` |
| BotHider | XBribo/CS2-Bot-Hider | `95b0dc0c82e1658b55e9b1e343d525ef8163873f` |
| BotVision | XBribo/CS2-Bot-Vision | `4c658ae07926a7a0fc3a87d83043cdac2ec91141` |
| BotAI | ed0ard/CS2-BotAI | `7d6997e11b952abb899d71abefa959c6e49cdfef` |
| BotAimImprover | ed0ard/CS2-Bullseye-Bot | `d796572b6d258b938a9d8db3c5d4c802a46049f7` |
| BotBuy | ed0ard/CS2-Bot-Buy | `11884e0564b8b57bee5f4729a5fca781bd5e6749` |
| BotRandomizer | ed0ard/CS2-Bot-Randomizer | `1c7c3ebad4b984bf8c4c75511b71cc109ab820bd` |
| BotState | ed0ard/CS2-Smarter-Bot | `e8c74af3bd2465478d6d07742f25ce53f4302bc7` |
| NadeSystem | ed0ard/CS2-Bot-NadeSystem | `7605f272d0500a7bad5b6294555485c7721a40da` |

## 保留的本地行为与适配

- BotState 保留同步前的主文件布局、自定义 FOV 实现及默认水平视角 120 度，合入上游
  1.10.4 的实时玩家槽位遍历、卡住位置分配优化和闪光实体生命周期缓存。
- 避闪继续使用本地的真实转头、延迟、回头及烟雾遮挡逻辑，闪白时长仍由游戏引擎决定。
  上游防止“后续避闪清除已有闪白”的修复针对其免疫路径；本地没有清除闪白的路径，保留该边界。
  实体缓存持续保留玩家控制器，保证闪光弹消失后仍能完成回头及状态释放。
- BotAI 保留本地 Windows 内外 FOV 补丁，其余重叠的内存偏移和签名采用上游版本。
- BotHider 合入上游带生命周期检查的展示写入和战队标识功能，保留本地自动赞成投票、
  托管机器人重生和早期刷新窗口。版本号直接使用上游的 `0.6.0`，适配本仓库没有 CMake
  元数据生成步骤的布局；API 项目引用指向本仓库的 `shared` 目录。
- 其余本地文件及自定义服务器配置保留，合入上游 Rush 模式、行为树、模式配置和文档更新。

## 验证

Windows / .NET SDK 10.0.400 下，以下项目均通过 Release 编译：
BotAI、BotAimImprover、BotBuy、BotRandomizer、BotState、NadeSystem、
BotControllerImpl、BotHiderImpl。BotState 保留同步前已有的 23 个警告，其余项目无警告，
所有项目均为 0 错误。另检查了变更 JSON 的语法、合并冲突标记及 Git 差异。

未在 CS2 进程中验证原生签名、游戏行为或 Linux 运行情况。
