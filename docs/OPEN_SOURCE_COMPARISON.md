# LC-06 独立开源对照环境

2026-09-04。**IN_PROGRESS：便携依赖、启动/停止和本地网页入口已核验；尚未配对、串流或取得真实 NX 延迟结果。** 当前 .NET/WGC/H.264/WS 主路线不变，没有将第三方进程集成进 Host/Agent。

## 固定来源

版本、commit、下载地址与 SHA256 见 config/open-source-comparison.lock.json。发布包哈希来自所选 GitHub release 的 expanded_assets，下载后逐包核对；commit 通过 git ls-remote 的 tag 引用取得，未再依赖已限流的 REST API。

| 项目 | 版本 / commit | 已校验 ZIP SHA256 |
| --- | --- | --- |
| [Sunshine](https://github.com/LizardByte/Sunshine/releases/tag/v2026.516.143833) | v2026.516.143833 / 14ffa6fdaa53f7b51512be2b3d24f3939695403c | 0a3af3dde43b8f2c94ffe04b850ad736d6e1be2b75906779d7094a5ad9d4783b |
| [moonlight-web-stream](https://github.com/MrCreativ3001/moonlight-web-stream/releases/tag/v2.10.0) | v2.10.0 / cd9d03cbf9a42b394f7b72a733a2f39cb5f0edd8 | 1dc3019952c610fbd7deb76dc84e3c4c6f26458ebb44823ea1f02ad883a36da9 |

已读取两个选定 commit 的 LICENSE，均为 GNU GPL v3 许可证文本，并保留 UPSTREAM-LICENSE.txt。此处只独立下载运行原发行版，本项目队列代码没有复制 Sunshine/noVNC 源码。依赖中其他组件许可与未来分发义务仍需发布前逐项核对；这不是把第三方便携包纳入本项目发布物的许可结论。Moonlight Web 是社区项目，不能作为官方 Sunshine 浏览器功能宣称。

配置依据所选版本 [Sunshine 配置文档](https://github.com/LizardByte/Sunshine/blob/v2026.516.143833/docs/configuration.md)、[Moonlight Web README](https://github.com/MrCreativ3001/moonlight-web-stream/blob/v2.10.0/README.md) 和两个实际 EXE 的 --help / print-config。

## 可复现准备与启动

```powershell
./scripts/prepare-open-source-comparison.ps1
./scripts/start-open-source-comparison.ps1
# 结束浏览器测试/释放输入后：
./scripts/stop-open-source-comparison.ps1
```

准备脚本不启动服务。归档缓存位于 .tools/comparison-downloads；运行目录位于 $env:LOCALAPPDATA/TwinDeskComparison/<时间戳>，项目 artifacts/comparison/latest.txt 指向它。运行文件、账户、密钥、日志不入 Git。每次 prepare 建新目录，已有记录进程须先 stop；可用 -Directory 指定以前准备的运行目录进行启动/停止。保留各次日志及原始失败证据。

启动使用 8092 网页、48989 Sunshine 基础端口、48990 HTTPS 管理端口及其协议偏移；先检查占用，不结束别的服务。两个程序作为当前用户的独立隐藏进程运行，没有安装 Windows 服务、驱动、防火墙规则、UPnP 或自启动。绑定仅 127.0.0.1，关闭默认 STUN，启用 WebRTC loopback candidates，关闭控制器/Steam 音频驱动安装和显示配置调整。启动实测只发现 loopback TCP 监听，UDP 尚未产生；首次真正串流后仍须重查子进程及 UDP 监听，不能把启动检查外推成所有传输都满足边界。

Sunshine 应用列表初始为空，不自动打开工程或启动桌面流。网页首次登录会创建其管理员；Sunshine 也需设置本次环境的本地管理凭据并配对。密码/PIN 由本地界面输入，不能写进提交或报告。管理页自签名证书只针对本地实验，不导入全系统信任根。

## 本机实测结果

- 失败1：Moonlight v2.10 拒绝只含 bind_address 的局部对象，报 missing field first_login_create_admin。改为调用所选 EXE 的 print-config 生成完整默认 schema，然后写入 loopback、独立端口和 streamer.exe 路径。
- 失败2：Sunshine 的 file_apps 相对自身 config 目录，而不是工作目录。已将空应用清单放到实际解析目录。
- 失败3：中文项目路径下无法创建 config/credentials/cakey.pem，HTTP 初始化失败；同包、同配置放到用户本地英文路径后成功启动。这是已观察到的路径兼容限制，未断言其上游根因；没有改用户项目名称或 Windows 用户目录。若新机器 LOCALAPPDATA 含非 ASCII 字符，脚本明确停止，需指定经审阅的英文独立路径后调整路径边界。
- 成功：C:/Users/happy/AppData/Local/TwinDeskComparison/20260904-160112-3740550/startup-report.json 记录最近启动检查；初次成功观察为16:01:25，随后又用相同脚本启动/停止验证了一轮。TCP为127.0.0.1:8092/48984/48989/48990/49010；Sunshine 启动自检发现 h264_qsv 与 hevc_qsv。编码器发现不是流性能结果。
- 浏览器实际打开 http://127.0.0.1:8092/，显示 Moonlight 用户名/密码登录表单。未提交登录、未配对。
- stop-report.json：16:04:07 所记录两个进程停止后，相关 TCP 监听为0。停机脚本核对 PID、可执行文件路径和启动时间，避免 PID 复用误停；测试过程中修复了 PowerShell 自动将 JSON 时间转成 DateTime 导致的误拒绝，以 UTC ticks 比较。配置与证据保留。

前两次失败日志保留在项目 artifacts/comparison/20260904-155505-0232344 与 20260904-155953-2550455；第三次为上述用户本地运行目录。当前验证结束后服务已停止，启动脚本可重开；不用旧页面是否存在判断服务状态。

## 配对与真实 A/B 的恢复步骤

1. 同一 Windows 用户、浏览器、硬件、可见 NX 隔离副本、相同窗口位置与分辨率。先记录 TwinDesk 新版 hash、队列指标和可见响应。现有 readonly 070-test.prt 不能冒充可写工程测试。
2. 停止 TwinDesk 接管并确认 F12/松键生效，启动上述独立服务，在本地设置两套实验凭据。Moonlight Web 添加 127.0.0.1，主机 HTTP 端口 48989；使用当次 PIN 完成 Sunshine 配对，不公开 PIN/令牌。
3. **先自有夹具验证同机焦点和指针。** Sunshine 原生捕获路径是显示器流，其应用启动器不构成 NX 窗口/弹窗隔离。仅在已清空其他内容的测试屏上显示夹具，再配置一个无启动命令/无 prep-command 的实验应用；不得将整个日常桌面纳入正式产品范围。若需要虚拟显示、驱动、另一登录会话或系统改动才能工作，记录失败条件与待审阅方案，不自动安装。
4. 先试 Web Sockets / H.264 / 1280×720，与当前主路径配置一致。确认浏览器安全上下文和实际 VideoDecoder 能力、硬编后端、帧率及实际呈现，再单独试 WebRTC。不能假定选择项已生效；不能把 WebRTC 的 localhost 配置当同机 SendInput/焦点兼容证明。
5. 先验证实体移动、中键拖动、滚轮、右键、键盘、松键、退出与恢复；重点观察网页 Pointer Lock/系统指针/目标前台是否争用、注入是否反馈回网页。夹具通过后才对可写 NX 副本做相同动作。没有可持续 NX 同机操作时，对照结论为“不满足当前同机控制前提”，不必强行比较一组无效延迟。
6. 记录可见响应 P50/P95/max/样本数、队列/掉帧、窗口/菜单/文件/中文/保存和失败原因。输入应答、视频帧计数和一次成功返回不能算 NX 工作流；P04≥200样本/≤200ms 与8小时门槛不变。
7. 浏览器正常停止流并释放输入，再运行 stop 脚本；保留日志。用户数据不删除，恢复 TwinDesk 用其独立 8091 启动脚本。本次对照无系统安装，因此回滚是停止已记录进程、回到原探针，不卸载驱动、不关闭 NX。

LC-06 下一项是上述同机物理输入兼容性与真实 A/B。此结果仅支持“可重复部署并启动对照服务”，不支持“已解决卡顿”或替换主路线。
