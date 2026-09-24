# LC-08 · 现成方案筛选与本机有限实测

2026-09-24。目标仍是：同机浏览器直接键鼠操控真实 NX，NX 自身最小化时仍有实时画面，宿主鼠标自由，不依赖第二块实体屏幕。本页是候选筛选与有限试验，**没有交付该目标**。所有截图/原始结果只保存在本机 `artifacts/verification`，不提交工程内容。

| 候选 | 已核对的能力 | 当前机器的结论 |
| --- | --- | --- |
| [Sunshine](https://github.com/LizardByte/Sunshine) + [moonlight-web-stream](https://github.com/MrCreativ3001/moonlight-web-stream) | 浏览器串流和低延迟硬编；项目内已固定版本、校验包并启动过本地入口，尚未配对或真实 NX 串流 | 同一宿主 Windows 桌面上仍共享系统输入与显示。已固定的 [Sunshine v2026.516 Windows 输入源码](https://github.com/LizardByte/Sunshine/blob/v2026.516.143833/src/platform/windows/input.cpp)对绝对/相对鼠标、按键、滚轮和键盘调用 `SendInput`；虚拟显示器只多出显示输出，不建立独立输入会话。不能解决 NX 自身最小化或宿主鼠标独立；配对也不能改变这个边界 |
| [noVNC](https://github.com/novnc/noVNC)、[Apache Guacamole](https://guacamole.apache.org/doc/gug/introduction.html) | 浏览器 VNC/RDP 客户端或网关 | 必须另有真正可工作的 VNC/RDP 主机。接入当前桌面仍共享鼠标；独立 RDP 会话依赖支持的宿主系统和图形环境，当前 Home 不能直接充当受支持的 RDP 主机 |
| [Duo](https://github.com/DuoStream/Duo) | 分离 Windows 桌面与输入的多席位串流，更接近“宿主鼠标自由” | GitHub 主仓库公开的是说明，不是完整可审计源码；安装过程使用 TermWrap、驱动/系统服务改动；免费单实例标称限 30 Hz。此处只核对公开说明，未在当前 Home 上安装、改服务或测试 NX。即使工作，NX 在独立会话内也需保持可见，不是 NX HWND 自身最小化 |
| [ChildStream](https://github.com/mattxslv/childstream)、[ParaDesk](https://github.com/sinpoce/ParaDesk) | 使用 Windows 子会话取得独立输入，ChildStream 搭配 Sunshine；ParaDesk 有只读探针 | 项目分别要求 Windows Pro 或更高；ChildStream 明示为概念验证，ParaDesk 需要管理员配置、RDP listener 和重启。当前 `CoreCountrySpecific` 为 Home，不能将其当成不改系统即可部署的方案；ParaDesk 的当前仓库/1.0 发布页许可证文字不一致，若用于商业工程还需澄清 |
| [Streamio](https://github.com/cloudomate/streamio) | 浏览器客户端、独立显示/输入架构 | 其 Windows 快速上手要求安装 VHID/间接显示驱动、启用测试签名及 GStreamer，`window_mgr` 仍标为 stub。不能当成熟、无需系统改动的 NX 方案；未安装或测试 |
| [西门子 NX on Azure/Citrix](https://blogs.sw.siemens.com/nx-design/nx-on-the-cloud-with-citrix-and-microsoft-azure/) | 有厂商参与的 GPU 虚拟工作站路径，独立会话本身可以让客户端鼠标与执行桌面分离 | 需要相应云/VDI 环境、图形能力及许可，已超出“同一台 Home 电脑直接运行现有 NX”的条件；本机没有账户/资源，也未产生 NX 10 真实验收。云端 NX 在其会话中仍保持可见，不满足字面最小化 |

## 本机试验

1. 真实 NX 10 `070-editable.prt` 当前进程 HWND 330432、PID 30044，源窗口 1107×709，文件标题显示内存修改未保存。先前只读 WGC 最小化 5.037 秒为 0 帧，见 [LC-08 可行性](./LC08_BACKGROUND_SESSION_FEASIBILITY.md)。
2. 新增只读脚本 [`observe-minimized-printwindow.ps1`](../scripts/observe-minimized-printwindow.ps1)：限制为已核对的 `ugraf` HWND，仅调用 Win32 `PrintWindow` 并保存一帧 PNG，不注入、不保存 NX 工程。可见时 `PrintWindow` 返回 true，完整 1107×709 模型画面；本地 `nx-printwindow-visible-20260924.png` SHA256 `994C4219447DC3D49DF69A6A652621A980E93876A264898F202D8946B6E6938C`。
3. 通过原生 UI 最小化同一 NX HWND，`IsIconic=true`。普通 flag 0 与 `PW_RENDERFULLCONTENT` flag 2 均返回 true，但各自 1107×709 PNG 只绘制 6,766 / 784,863 像素（0.862%），其余保持测试品红底色；图中没有模型、菜单或可用完整界面。报告图分别为 `nx-printwindow-minimized-flags0-20260924.png`（SHA256 `86E95EC926F3515245189666EA61F44DA42FAD337B8E78DFFC6083CFC699D9A3`）和 `nx-printwindow-minimized-flags2-20260924.png`（`9A0FCA566266475C36C57D5DF6256F6A90F9E5E13649AE5E8C060DA186981E19`）。仅凭 API 成功返回码会误判，不能把这 0.862% 当实时最小化画面。
4. 只读调用微软 [`WTSIsChildSessionsEnabled`](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsischildsessionsenabled) 成功，返回 enabled=0；系统 `EditionID=CoreCountrySpecific`、build 26200，`TermService=Stopped`、RDP listener 未启用。这不单独证明每种第三方实现都失败，但结合候选明确要求 Pro/驱动/服务改动，当前没有可立即启动的独立会话候选。没有调用会修改系统的 `WTSEnableChildSessions`。
5. 已通过原生 UI 恢复同一 NX HWND，重新枚举 `IsIconic=false`、窗口仍 1107×709、进程启动身份不变；未保存、关闭或修改模型。8091 现有试用服务保持原状态。上述测试没有取得浏览器控制 NX、宿主鼠标独立或动态低延迟的通过证据。

## 下一步门槛

当前 Home 环境不安装 Duo/Streamio 的服务/驱动/测试签名，不购买许可或更改 Windows 版本。若用户接受“宿主不显示 NX、但 NX 在隔离会话内保持可见”的体验定义，并同意必要系统/许可变更，再优先选可撤销、受支持的独立会话路线；先运行自有夹具与浏览器输入，再以 NX 隔离副本核对图形、许可证、原生菜单和保存。若字面要求是 NX **所属会话内确实最小化**，本轮 WGC/PrintWindow 双负例和上述现成项目均未提供可交付方案。
