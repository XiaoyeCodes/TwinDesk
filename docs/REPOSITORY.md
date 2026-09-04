# TwinDesk 仓库说明

> **当前执行入口：** [本机任务计划 LOCAL-1](./LOCAL_IMPLEMENTATION_PLAN.md) 定义 LC-01～07、原 A/P 验收的本机映射和路线变更门槛；[开发交接](./DEVELOPMENT_HANDOFF.md) 定义换电脑恢复步骤。与旧文中异机/CF专属要求冲突时，以用户范围变更及 LOCAL-1 映射为准；其余安全、真实性和完成条件保留。

本项目用于单人在同一台 Windows 电脑通过浏览器操作 Siemens NX / TIA Portal。当前为 M0/M1 技术验证阶段，不是可部署的完整远控产品。

## 文档入口

- [完整规划书](./CODEX_AUTOMATION_PLAN.md)
- [实现契约与任务依赖](./IMPLEMENTATION_CONTRACTS.md)
- [验收标准](./ACCEPTANCE.md)
- [当前状态与明天续接点](./PROJECT_STATUS.md)
- [目标启动/续接正文](./GOAL_PROMPT.md)
- [关联场景历史证据说明](./M1_SCENE_EVIDENCE.md)
- [当前场景夹具证据](./M1_FIXTURE_EVIDENCE.md)
- [输入核心证据与下一步](./M1_INPUT_CORE_EVIDENCE.md)
- [原生输入阶段证据](./M1_NATIVE_INPUT_EVIDENCE.md)

根目录 README.md、server.py、index.html 是原始 Python Demo，保留其原始内容；`legacy/demo-v3` 为基线副本。它们不是当前 .NET 媒体探针的运行入口，也不是生产部署指南。

## 克隆与构建

在 Windows x64、PowerShell 7 环境下，进入克隆目录后执行：

```powershell
./scripts/bootstrap.ps1
./scripts/verify-probe.ps1
./scripts/verify-media.ps1
./scripts/verify-input.ps1
```

bootstrap 按锁定版本准备项目本地 .NET / Node 工具链；需要联网下载，但不替换系统已有 SDK。已有 `.tools` 时校验复用。verify-probe 执行锁定依赖恢复、构建与本机原生能力冒烟；verify-media 含真实 GPU Alpha 像素、硬件/软件编码验证，缺少相应 Windows/GPU 能力时可能失败，不可跳过后声称通过。执行前可先阅读这些脚本。

没有 NX/TIA、许可证或原电脑测试产物，仍可读取设计和构建源码；但不能据此宣称真实应用兼容或最终验收通过。真实软件测试遵守工程副本、保存保护及不向真实 PLC 写入的约束。

探针使用说明见 `tools/Workbench.Probe/README.md` 和 `tools/Workbench.MediaProbe/README.md`。媒体探针仅监听本机回环地址，没有产品鉴权和键鼠输入，禁止直接暴露 LAN/公网。

输入核心脚本只运行逻辑测试，不注入键鼠。场景夹具另用 `./scripts/verify-scene.ps1 -Cycles 40`，会在已登录的 Windows 图形会话中短暂显示自己的测试窗口；包括自身临时置顶遮挡窗，不操作 NX/TIA。交互 F0 目标与详细边界见 `tools/Workbench.DesktopFixture/README.md`。

## Git 与本机文件边界

仓库包含源码、依赖锁文件、验证脚本、规划和测试结果说明。不包含本机 `.tools`、NuGet/npm 缓存、bin/obj、artifacts、安装包、工程文件、截图、密钥或环境凭据。原电脑的实测 JSON/码流留在 `artifacts/verification`，因此新克隆中相关历史证据链接可能不存在；需要在新环境按脚本重新生成，不能复制历史 PASS 充当新机器结果。

另有 `./scripts/verify-native-input.ps1`，需要显式点击测试窗 Start，会通过项目后端向自身测试窗口实际注入键鼠；运行期间请不要操作键鼠。它不是无人值守逻辑测试，也不验证 NX/TIA。

具体外部条件和下一项以 PROJECT_STATUS 最新入口为准。有界执行器、捕获绑定和同机实验入口已有实现；接下来按 LC-01～07 解决时延与可用性，保留 SC07/SC08 门槛，不能直接跳到正式 M2。

## 锁定前端工具链

运行 ./scripts/verify-web-dependencies.ps1，在新的临时验证目录执行npm ci、严格TSX类型检查、React中文服务端渲染及Vite生产构建，并检查锁文件不变。依赖定义和说明在src/web；这里只准备工具链，正式React控制台仍待M1门槛。

NX输入的显式有限实验入口为 ./scripts/start-nx-input-probe.ps1 -VerifiedCopy <artifacts/verification中的已核对可写prt> -FullHd。先用原生NX确认打开的是完整副本路径；脚本不打开工程、不抢焦点。标题准入不能证明路径或限制原生另存为位置。普通媒体仍只读，两个输入入口都不得暴露LAN/公网。当前后端已有有界执行器与捕获绑定，下一步按状态表继续LC-01，以上早期“尚无输入”描述仅适用于只读入口。
