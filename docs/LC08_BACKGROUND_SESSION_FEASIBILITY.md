# LC-08 · 浏览器独占操作与最小化 NX 可行性

状态：`IN_PROGRESS / WAITING_EXTERNAL`。用户于 2026-09-24 同意**试验**“宿主只显示浏览器、NX 在独立会话内保持可见”的等效体验，最终要在同一浏览器页面同时显示、分别操作 NX 和博图。随后明确最近的试用目标为**左右两个独立 NX 10 实例**，鼠标在浏览器整页自由移动，进入哪侧就操作哪侧；一块实体显示器即可。试验许可不等于真实功能已通过验收，也不将“自动还原”“移出屏幕”或截图缓存写成最小化实时控制已实现。

## 要求与现有实现

用户要求同一台电脑上：NX 在 Windows 最小化后仍有实时原生画面；点击网页连接即可直接用浏览器键鼠操作；NX 不必占当前前台或另一块实体屏幕；宿主实体鼠标可自由移动。真实 NX 菜单、绘图区、文件框、中文、保存和安全释放仍须保留。

当前 `LocalConsoleBridge` 拦截宿主物理键鼠，浏览器经控制 WebSocket 送回 `SendInput`，NX 必须在当前输入桌面前台；`OwnedWindowScene.Select` 和原生输入校验拒绝最小化。浏览器直送 PointerEvent 的旧分支虽存在，同一 Windows 输入桌面上点击浏览器即使 NX 失去前台，也不能获得独立第二套系统鼠标/键盘焦点。不能简单删除前台/最小化检查。

## 已取得的真实证据

- 2026-09-24 对运行中的真实 NX 10、已核对的可写隔离 `070-editable.prt`，先最小化，再以精确 HWND/进程启动身份新建只读 WGC 会话。`artifacts/verification/nx-minimized-wgc-20260924.json`（SHA256 `5ED47AD3E67AECBBB183ADB7A1A2CC9CDF805E34007CC146BAE2168567C64375`）记录 `IsIconic=true`、捕获项 159×27、5.037 秒 0 帧。探针无像素输出和输入。随后用原生 UI 恢复，重新枚举确认 `IsIconic=false`，没有保存或关闭工程。有限观察不代替所有 Windows/NX 配置，但直接否定本机当前窗口的“只去掉最小化校验即可继续串流”。
- [微软 WGC Win32 HWND 官方示例](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/cpp/ScreenCaptureforHWND/README.md)注明最小化窗口可以枚举，但不会采集。
- [微软 SendInput 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)说明事件进入键鼠输入流；[Windows Desktops 文档](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops)说明当前活动桌面接收用户输入。由此推断同一活动桌面内直接注入无法同时保持宿主浏览器自由焦点与后台 NX 完整原生交互。该推断仍需在候选隔离环境中验证实际 NX 行为。
- [PrintWindow 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)仅说明目标应用按 `WM_PRINT` 绘制到 DC，是同步调用；尚无 NX OpenGL 动态画面、原生菜单与后台输入可用的证据。DXGI 显示器复制、Sunshine、本机虚拟显示器只能取得仍在显示输出上的内容；单独使用都不提供第二套独立输入桌面。
- 后续对同一真实 NX 做了只读 `PrintWindow` 对照：可见时取到完整模型；真正最小化时普通与 `PW_RENDERFULLCONTENT` 两种调用虽都返回成功，只绘制 0.862% 的左上角区域，绘图区仍为空。见 [现成方案筛选与本机试验](./LC08_OPEN_SOURCE_TRIAL_20260924.md)。因此不能把 `PrintWindow` 成功返回码当作最小化实时画面。

## 候选路线与真实含义

候选为**同一物理电脑上的隔离 Windows 图形会话/虚拟机**：NX 在隔离会话中保持前台且不最小化；该会话内的 Agent 采集 NX 窗口/关联弹窗并接受浏览器键鼠；宿主浏览器仅显示真实流，宿主鼠标不安装全局钩子，也不被 `SendInput` 改写。继续保留 WGC、H.264/WebCodecs、JPEG 兼容、单控制会话及身份/输入释放。宿主只提供本机受控入口与隔离会话转发，不能把整套客体桌面公开成产品界面。

当前运行在宿主会话的 NX 进程不能靠浏览器“连接”搬进另一会话；候选首次试验需要在隔离会话另启动 NX，并以隔离模型副本打开。现有 `070-editable.prt` 的未保存内存修改必须保留，不能为了迁移强制关闭或自动保存；如要连续使用该状态，需要用户先在原生 NX 中确认如何保存为新副本，且核对额外 NX 会话/许可证是否允许。

这可以满足“不占宿主前台、无需第二块实体屏幕、宿主鼠标自由”的体验目标，**不能满足“正在受控的 NX 自身在所属 Windows 会话中仍处于真正最小化”这一字面条件**。NX 必须在它自己的图形会话里可见并渲染，且那个会话必须有独立输入焦点。若 Windows 任务栏的实际最小化状态本身是不可改的验收条件，目前没有可交付的完整原生 NX GUI 方案；不能拿自动还原、虚拟显示器或缓存帧替代。

宿主环境只读预检：Windows 11 家庭中文版 build 26200、32 GB 内存、D 盘约 75 GB 可用，未找到已安装的 VirtualBox/VMware CLI；系统报告存在 hypervisor，但是否能运行满足 NX 图形要求的客体未验证。现有 `OrayIddDriver Device` 不能证明独立 Windows 会话或输入。Windows Home 不在[微软支持的 RDP 主机列表](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-supported-config)，不采用绕过授权的 RDP 补丁。

2026-09-24 补充只读核对：注册表 `EditionID=CoreCountrySpecific`、build 26200/25H2，显示设备仅列 Intel Iris Xe 与 Oray 虚拟显示设备；未发现 Hyper-V 管理命令或常见 VirtualBox/VMware/QEMU CLI。[微软 Hyper-V 安装文档](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v)明确 Windows Home 不能安装 Hyper-V 角色，因此“检测到 hypervisor”不等于可用 Hyper-V VM 主机。其他虚拟机软件是否可安装、是否提供 NX 需要的客体 GPU 能力仍未知。

厂商依据的范围也必须分清：[西门子 2014 年 NX 私有云公告](https://blogs.sw.siemens.com/designcenter/siemens-press-release-siemens-nx-software-now-available-in-cost-effective-private-cloud-environment/)涉及 NVIDIA GRID vGPU 的认证 VDI；[西门子 2016 年说明](https://blogs.sw.siemens.com/simcenter/run-nx-from-the-security-of-the-data-center-with-amd-multiuser-gpu/)涉及 AMD MxGPU；[西门子 Azure 方案](https://blogs.sw.siemens.com/designcenter/siemens-nx-on-the-cloud-with-microsoft-azure/)使用 GPU 虚拟机。这些证明“有特定 GPU 虚拟化环境能运行 NX”的路线存在，**不能证明本机 NX 10 + Intel Iris Xe + Windows Home + 尚未选定的客体图形驱动受支持或性能达标**。未找到能覆盖这一具体组合的厂商认证材料，因此不安装/迁移 NX 许可证、不承诺本机 VM 方案可用。

## 下一步与外部门槛

1. 试验定义已获用户同意：先在一个独立会话内启动**两个不同 PID/启动身份/根 HWND 的 NX 10 实例**，分别打开不同的隔离 `.prt` 副本；浏览器同页左右等宽显示两条真实窗口流。网页指针不限制在单侧；单实体鼠标一次只控制所选一侧，移到另一侧并点击时先释放旧侧按键，再核对新进程/窗口/场景并取得唯一输入租约。不能复制同一 NX 画面两次、把两个标签页称为两个实例，或靠标题字符串选择目标。宿主实体鼠标始终自由。单实例时网页占满工作区。两软件不可能同时拥有 Windows 前台焦点，但后台一侧画面是否可靠持续更新必须实测；不能以两幅静态图代替。随后将右侧换成真实 TIA 进程/工程，重复左右切换与完整工作流。NX 在隔离会话内保持非最小化，不把本试验标为字面最小化条件通过。
2. 在不购买/安装/迁移许可前，取得可审阅的 Windows 客体许可、NX 10 在**拟采用的具体虚拟 GPU/驱动与客体 OS**中的厂商支持/许可证条件、可在 Home 宿主上合法使用的虚拟化软件和隔离测试工程。若拟用 Hyper-V，需先由用户决定并批准支持该角色的宿主 Windows 版本变更；不能把现有 hypervisor 标志当作已经具备 Hyper-V。额外费用、系统版本变化及许可证操作需单独批准。
3. 外部条件就绪后，先在隔离会话用自有 F0 夹具验证真实窗口持续 WGC/编码/浏览器解码、原生弹窗、浏览器 PointerEvent 与宿主鼠标互不干扰；再用隔离 `.prt` 验证真实 NX 视图、右键/子菜单、文件框、中文、编辑与保存。每一步记录实际宿主/客体前台、鼠标位置、按键释放、帧与可见响应，不以成功返回码代替。
4. 候选路径过门槛后才设计宿主 loopback 反向代理与客体 Agent 的认证/租约，保持单应用画面而非整桌面遥控。验证 8 小时和原必需验收项后才可替换当前模式。

2026-09-24 新增可复跑的只读预检 `scripts/check-isolated-session-readiness.ps1`。当前报告 `artifacts/verification/isolated-session-readiness-20260924.json`（SHA256 `732A1F7B6BFF569C50E5C805D1EC08C3AEE115CAF0AFA827FA872E241525AA8E`）记录：`CoreCountrySpecific`/25H2、受支持 RDP 主机版本判定 false、`WTSIsChildSessionsEnabled` 查询成功但 enabled=false、TermService stopped、RDP 拒绝连接；主控台会话 1 中仅 NX PID 30044 有主窗口；所查常见 TIA 进程名下无运行实例。`canStartSupportedChildSessionTrialNow=false`、`twoNxInOneIsolatedSessionObserved=false`。此判定是宿主前置条件，不代表“所有 Windows Home 子会话 API 一定无法调用”；没有调用会改系统的启用函数，也没有安装软件或操作 NX 工程。

恢复顺序：用户准备并授权受支持的 Windows Pro/Enterprise/Education 宿主环境（或另选经核实可用的隔离图形会话宿主），明确任何升级费用/系统变更；确认 NX 10 许可允许两个进程/会话并行使用，准备两个不覆盖原件的 `.prt` 副本，核实 NX 10 在具体会话 GPU/驱动/系统上的厂商支持。之后复跑该预检，以自有 F0 夹具验证会话创建、浏览器输入/视频和宿主鼠标分离，再启动两个真实 NX 实例做双流与切换验收。博图阶段另需受支持的组件/许可证/离线工程，右侧替换为 TIA 后再核对完整工作流。现有未保存 NX 进程不迁移、不关闭、不自动保存。若不能提供上述条件，LC-08 的独立会话真实软件验证维持 `WAITING_EXTERNAL`，同时继续 LC-04/05 等不依赖项。

当前源码 `--observe-minimized` 为可复跑的**只读** WGC 帧到达诊断，不改变正式控制的最小化拒绝。报告只留本地 artifacts，不提交工程内容。现有 8091 试用服务继续使用可见 NX；不得把候选路线写成可用功能。

现成方案新增核查见 [LC-08 现成方案与本机试验](./LC08_OPEN_SOURCE_TRIAL_20260924.md)：Sunshine/noVNC/Guacamole 在当前桌面不生成独立输入；Duo/ChildStream/ParaDesk 接近独立会话体验，但分别需要 TermWrap/系统改动或 Pro 版；Streamio 要求驱动和测试签名。已只读核对本机 Home、WTS 子会话未启用，没有安装或启用这些候选。
