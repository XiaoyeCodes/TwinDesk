# 同机浏览器键鼠实验

这是按 2026-09-04 用户追加要求实现的 **同机双屏候选入口**。使用已连接的两个显示器，NX 保持原生前台，浏览器显示真实 WGC/H.264 画面。已收到实体鼠标事件，尚未取得完整可见工作流通过证据，不能称为已验收远控，也不替代 SC07、异机或 8 小时测试。

## 开始体验

1. 原生 NX 打开隔离副本 `artifacts/verification/nx-sample-20260904-102633-6240000/070-editable.prt`。先核对完整路径。当前已打开这个副本；原始 `Desktop/070.prt` 未改动。
2. 项目目录运行 `./scripts/start-local-nx-console.ps1`。默认 H.264/720p；显式 `-Jpeg` 为兼容模式，`-FullHd` 选择 1080p。已启动服务时不要重复启动。
3. 打开 `http://127.0.0.1:8091/`。浏览器与 NX 分别放在两个屏幕，NX 不得被其他窗口挡住。保持网页可见，不切换标签页。
4. 点击“连接并接管 NX”，等待真实画面和橙色十字。程序尝试一次激活 NX；若 Windows 拒绝，按网页提示点击原生 NX 标题栏一次并松开键鼠，无准备倒计时。
5. 此后看网页的橙色十字操作。先在绘图区移动、滚轮缩放、中键小幅拖动，再试右键菜单与 Escape。按 **F12** 退出接管后，鼠标恢复正常，才可点击网页按钮或其他应用。

不设固定体验时长；失败后需重新开始。F12、停止视频、关闭标签页、控制断线也会结束接管。故障检测和输入释放期限仍保留，尚未完成长时间运行验证。接管期间系统原生指针会在 NX 内移动，网页橙色十字仅表示请求位置，实际 NX 画面变化才是操作效果。不要把橙色十字移动本身视为 NX 控制成功。

如果启动失败、十字不动或 NX 没有响应，先按 F12，记录网页错误。不要删除焦点/物理命中保护，不要反复盲点。该模式未验证单显示器重叠窗口，不承诺隐藏/最小化 NX 后仍可操作。

## 本次设计增量

原定异机路径中浏览器鼠标与被控机鼠标是两套设备。同一 Windows 会话中的常规浏览器与 NX 共用焦点/指针，因此增加显式 `--local-console` 实验模式：

实体键鼠 → 有限原生输入桥 → 本机控制 WebSocket → 网页坐标与按键逻辑 → 原有 InputSession/SendInput → NX → WGC/硬编 → 网页。

输入桥不直接调用 NX 命令，不改变 WGC/H.264 主路线，不改为 PostMessage 后台操控。只有本地启动开关和网页显式接管同时满足时才安装低级钩子；回调跳过注入事件、防止循环，只使用有界队列，不记录按键正文。保留进程/副本、场景、显示确认、原生焦点、命中和权限检查。用户点击连接后仅尝试一次 SetForegroundWindow；系统拒绝时等待用户原生点击，不循环争抢前台。

F12 在原生桥退出；桥有独立半秒存活期限和原生前台检查，控制心跳、场景和视频退出沿用原有释放链。新增 InputSession 持键期间周期性原生检查，焦点丢失即释放，即使浏览器没有再发送输入。

本模式要求显示器布局和 NX 可见区域满足原生命中。它属于用户明确要求的追加实验能力，不改变主规划 M0–M8 完成条件。中文、复杂弹窗、DPI 完整矩阵和连续建模保存均保留待验收状态。

## 已有证据与限制

- C# 输入/场景测试 160 项通过，浏览器逻辑测试 48 项通过；两者均不是人手 NX 验收。
- 自有 Windows 夹具实际安装钩子，3 个注入事件均跳过转发，实际控件收到按下/释放；停刷新后 watchdog 退出，系统 A 键未保持按下。见 `artifacts/verification/local-console-native-*/report.json`；早期失败报告保留。
- 原生测试首先发现全虚拟键枚举把 VK 85 的既有状态作为启动条件。准入现按既有支持扫描码集合、鼠标按钮和 Windows 修饰键检查；未支持键不转发，遇到实体未支持键结束模式。没有擅自释放该既有键。
- 自动化工具产生的是注入事件，本模式应跳过它们。因此工具点击不能充当实体设备测试；移动增量、实际 F12、真实 NX 连续操作需要用户在上述页面亲测。
- Windows 可能因低级钩子回调超时而静默移除钩子，系统不提供可靠的移除通知。本模式使用独立消息线程和短回调降低此风险，但不能据此宣告耐久通过。

实现依据：[LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)、[LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)、[MSLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct)、[KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct)。

原生夹具可复运行：先构建 `tools/Workbench.DesktopFixture`，执行其 DLL 的 `--verify-local-console <新的证据目录>`，将该测试窗口置于前台。脚本启动不会自动抢占已有工作窗口。

回滚此实验：停止服务，按原 `start-nx-input-probe.ps1 -VerifiedCopy ...` 启动而不传 `-LocalConsole`；不会安装输入桥，不修改 NX 工程。
