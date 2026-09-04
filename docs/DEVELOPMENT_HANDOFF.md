# 换电脑继续开发交接

更新：2026-09-04。产品现在仅要求本机浏览器控制本机真实 NX/TIA；在另一台电脑继续写代码与测试，不等于恢复跨电脑远控需求。

## 先确认拿到了最新工作区

最新源码同步检查点位于 main，父提交为 e93ad38af2a4a72a3a149553993b49b9825a6b64，提交标题为 Improve local input scheduling and prepare isolated streaming comparison。包含本轮队列/统计/回归测试、第三方便携包锁定与准备/启停脚本、证据范围及续接文档。用户下班前已明确授权提交推送；接收端以 git log -1 与 origin/main 核对本检查点或后续版本，不能只拿到旧 e93ad38。旧检查点包含此前本机输入、场景修复、前端锁与文档，已确认推送成功。

迁移前应核对工作区内容；本检查点推送成功后可通过 Git 携带源代码、脚本、测试、docs、src/web 与依赖锁文件。若之后又有未提交工作，需另行同步。不要只复制本文。保留未跟踪文件；不要 reset/clean 覆盖用户工作。不要提交密钥、令牌、许可证或用户工程文件。

.gitignore 排除了 .tools、bin/obj、node_modules、artifacts 等。新电脑缺少这些并非源码丢失：工具链可重建；原始 JSON/码流/研究快照需要单独携带，或明确标历史证据未随迁移。不得重新生成一个历史 PASS，伪装成原报告。

原电脑测试副本路径为 artifacts/verification/nx-sample-20260904-102633-6240000/070-editable.prt；此前观察它有未保存修改，本轮最后看到 NX 当前为 070-test.prt（只读），没有推断之前修改是否已保存。磁盘复制不会带走 NX 进程内未保存内容，保存位置由用户决定，迁移/测试程序不得擅自保存或关闭。原件 Desktop/070.prt 禁止作为写入目标。

## 新电脑恢复顺序

1. 进入实际项目目录，不要求沿用 C:/Users/happy 的绝对路径。读 PROJECT_STATUS 最新入口、LOCAL_IMPLEMENTATION_PLAN、GITHUB_LOCAL_CONTROL_RESEARCH，再读主规划、契约、验收与 REFERENCES。
2. 检查 git status --short、git rev-parse HEAD；确认 LocalConsoleBridge.cs、LoopbackInputProbe.cs、input-client.js、SceneRefreshVerification.cs、start-local-nx-console.ps1 和新任务文档存在。有更新记录时以更新记录为准，不回退到本交接快照。
3. 使用 Windows x64 / PowerShell 环境。先检查 config/dependencies.lock.json 与 global.json，再运行 ./scripts/bootstrap.ps1，复用并校验已有 .tools。不要将系统 SDK 版本直接当 .NET 10 目标版本。
4. 先构建及逻辑回归。下面命令不启动 NX 控制，不要求真实 NX：

```powershell
./scripts/bootstrap.ps1
. ./scripts/environment.ps1
& $Dotnet restore tests/Workbench.Windows.Tests --locked-mode
& $Dotnet test tests/Workbench.Windows.Tests --no-restore
& $Node --test tests/input-client.test.cjs tests/local-console.test.cjs tests/frame-presenter.test.cjs tests/scene-timeline.test.cjs
& $Dotnet restore tools/Workbench.MediaProbe --locked-mode
& $Dotnet build tools/Workbench.MediaProbe --no-restore
```

每条检查退出码，失败就定位，不把后续命令成功当整组成功。上一版 168 C# / 50 完整 JS 是历史测试数量，上述 JS 为针对性子集，不能据此声称已重跑50项。

最新公共回归可直接运行 ./scripts/verify-local-input-queue.ps1，当前已验证 C#174/JS60。队列/脚本/证据文档已纳入本次同步检查点，回家核对提交标题及文件，不重做已完成公共实现。原始 artifacts 与用户模型不随 Git；没有携带的原始报告应标为历史摘要证据，不重新制造原机 PASS。

5. 有 Windows 图形/GPU 条件再运行自有夹具；若没有 NX/TIA，继续 LC-01 埋点和 LC-02/03 的独立代码任务，对 nx/tia 子项标 WAITING_EXTERNAL。若不是 Windows，可阅读/修改/执行适用前端测试，但不得声称 Windows 构建或原生测试通过。
6. 有可用 NX 后，由原生界面核对隔离模型副本完整路径，保持 NX 可见，再启动下述入口。不要复用旧 HWND/PID、旧进程状态或旧机器测量：

```powershell
./scripts/start-local-nx-console.ps1 -VerifiedCopy '<本机已核对的隔离副本绝对路径>'
```

7. 打开 http://127.0.0.1:8091/，点击“连接并接管 NX”，F12退出。目前候选实现要求 NX 与浏览器在不同物理显示器，单显示器模式尚未验证。新机器硬编、DPI、焦点、窗口/弹窗行为均需重新确认；缺条件时继续独立工作，不降低验收。

8. 独立开源对照按 OPEN_SOURCE_COMPARISON.md 运行 prepare/start/stop-open-source-comparison.ps1。固定版本 ZIP 可重新下载校验，实际运行目录在用户 LOCALAPPDATA/TwinDeskComparison 的英文路径。首次登录/配对与真实串流尚未执行；不要继承旧主机的证书、账户、进程或客户端配对。原电脑 8091 与本次对照服务在交接前均已停止，NX和用户已有其他进程保持原状。

## 当前接着做什么

从 LC-01 剩余实际测量及 LC-02/03 真实复测继续；有序移动合并、SPSC物理桥与分段统计已实现，C#174/JS60及自有原生桥有限测试通过，见 LOCAL_INPUT_QUEUE_EVIDENCE.md。LC-04依实际 nativeChecks 开销推进。独立开源便携环境启动/停止已验证，LC-06实际同机输入与NX A/B仍待，见 OPEN_SOURCE_COMPARISON.md；不要从零重写已完成公共部分。

真实 NX/TIA 操作、菜单/中文/保存、双应用布局、释放恢复、8小时和发布交付仍未全部验收。缺 TIA/许可/系统匹配/工程时，准备精确恢复步骤；不改 OS、不买许可、不向 PLC 下载/运行。整项目标仍未完成。

可直接给下一次开发会话的正文见 GOAL_PROMPT.md。阅读完成后自行推进最近未完成且依赖已满足的任务，不重复需求访谈；每个连贯任务更新状态与下一步。
