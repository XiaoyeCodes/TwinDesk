# M1 初始实测记录

2026-09-03 15:16 历史快照 · **M1 未完成**。这里只记录当时执行的局部实验，不替代 ACCEPTANCE.md。后续实际编码与浏览器实验以 PROJECT_STATUS 的更新及 M1_MEDIA_EVIDENCE.md 为准，不从本历史记录推断后续工作尚未进行。

## 1. 已执行

| 实验 | 实际结果 | 本地证据 |
| --- | --- | --- |
| 当前两项目构建 | 修复 D3D11 重载调用后，0 错误、0 警告；locked restore 可复跑 | probe-* 目录中的 build.binlog |
| NX 进程/窗口识别 | 找到 NX 10.0.0.24、只读 070.prt；最初最小化，恢复后可采集 | 首帧 PNG、后续 nx.json 的 Target |
| 项目 WGC 首帧 | 1632×727，视觉检查完整工具区、导航区与三维模型，未黑屏；首次约 42.2 ms | m1-wgc-nx-first.png/.json |
| 再次窗口采集 | 仍返回 NX 界面；模型位置已变化。此间其他前台内容出现，不作为受控遮挡/动态实验通过 | m1-wgc-nx-occluded.png/.json |
| 原生显示拓扑 | 2 输出；主屏 2560×1440/120 DPI，第二屏 1920×1080/96 DPI；NX 窗口自身报告 120 DPI | m0-displays.json |
| H264 候选 | 两个 Intel Quick Sync 硬件类别实例、一个软件实例均成功 ActivateObject | m1-encoder-activation.json |
| 探针冒烟检查 | 构建、帮助、无效参数、不存在进程、显示枚举、禁止覆盖证据、编码枚举报告、可选 NX 首帧 | scripts/verify-probe.ps1；probe-*/report.json |

所有证据均在 `artifacts/verification/` 下，未上传，未修改模型文件。Windows 应用操作使用 Computer Use 恢复/观察；项目自己的 WGC 调用生成 PNG，不以工具截图冒充项目采集结果。

## 2. 不得从这些结果推断的能力

- 约 32–42 ms 是部分样本中从采集启动到第一帧的时间，不含登录/网络/输入，不是完整远控时延。
- 只读模型静止时观察到 1 帧；早期 JSON 的 averageFps 为观察期平均到达数，不能作为渲染性能。后续已改为 frameArrivalRate 并附测量说明。
- 尚未证明旋转/缩放等运动持续可采、菜单/弹窗可见可控、DPI 点位正确、双应用切换正确。
- 编码器激活尚未配置输入/输出类型，不证明它已接受纹理、产出 H264，或浏览器可解码。
- 当前探针仅保存第一帧，不是录屏；窗口改变尺寸会明确失败，动态重建尚未实现。
- TIA、第二台客户端、8 小时与 CF 均未执行，不标 PASS。

## 3. 复跑

```powershell
.\scripts\verify-probe.ps1
.\scripts\verify-probe.ps1 -CaptureNx
.\scripts\doctor.ps1
```

第一条不占用 NX 输入。第二条要求 NX 已打开且未最小化，只采画面，不注入。doctor 不改变显示、软件、许可证或安全配置。每次自动生成新的本地报告目录/文件。

下一步：硬编实际喂帧/出码流验证，同时准备可控 Windows 夹具供持续采集与输入测试；真实 NX 菜单/弹窗测试须在明确的桌面测试时段继续，不碰唯一工程原件。
