# M1 实际编码与浏览器媒体链路证据

2026-09-03 · M1-03 局部进展，**M1 仍未完成**。

## 1. 实现范围

- `src/Workbench.Windows/H264Probe.cs`：真实 Media Foundation 编码，异步硬件事件驱动流程、同步软件流程、输出类型重新协商、排空、取消和 COM 资源释放。
- `AnnexBAccessUnits.cs`：本探针单 SPS/PPS 固定格式会话的 Annex B 整理，实际 SPS 导出 codecString、IDR 参数集补齐、大小/时间戳/头部边界检查。不是完整 AVC 语法验证器。
- `tools/Workbench.Probe --encode-test`：明确选择硬件或软件，有限长 NV12 动态图编码并保存码流和逐帧索引；不是录像文件回放。
- `tools/Workbench.MediaProbe`：实时产生 → 真实硬编 → WebSocket v1 包 → 浏览器 WebCodecs → Canvas。页面根据真实解码像素检查移动方块的位置。
- 本机入口仅 `127.0.0.1:8091`，固定生成内容，严格 Origin/Host，一次一个编码连接，无桌面输入或窗口读取。未实现正式产品鉴权，不得发布到局域网或公网。

本次没有操作 NX 模型、安装/配置 TIA、变更许可证、显示器或 CF。

## 2. 实际结果

所有下列路径均在 `artifacts/verification/` 内。浏览器为 Codex 内置 Chromium，报告 UA 为 Chrome/152.0.0.0；没有用它冒充独立电脑上的 Chrome/Edge 验收。

| 实验 | 结果 | 证据 |
| --- | --- | --- |
| 首次硬件 30 帧 | 30 入 / 30 出；Intel Quick Sync；1280×720 / 30 FPS 配置；实际 `avc1.42401F` | `m1-h264-hardware-03.json/.h264` |
| 10 秒浏览器实验 | 300 收 / 300 解码；11 次像素检查、0 错误 | `media-probe/run-20260903-153801-4822775.json` |
| 10 分钟浏览器实验 | 18000 入 / 出 / 收 / 解码；601 次像素检查、0 错误；299498832 字节实际 H264 输出 | `media-probe/run-20260903-154856-8797540.json` |
| 最终边界保护版本 10 秒复测 | 300 收 / 300 解码；11 次像素检查、0 错误；记录一次输出格式重新协商 | `media-probe/run-20260903-155021-8265562.json` |
| 取消后同进程 60 秒重连 | 1800 收 / 1800 解码；61 次像素检查、0 错误；服务进程未重启 | `media-probe/run-20260903-155200-3274576.json` |
| 码流/参数单元测试 | 20 PASS，0 FAIL，0 SKIP | `media-20260903-154744-8990928/unit-tests.trx` |
| 硬件/软件回归 | 各 90 入 / 90 出；硬编首次输出约 75 ms，软件约 568 ms；无自动降级 | 同目录 `report.json`、`hardware.json`、`software.json` |
| 无效参数/证据保护 | 0 帧在创建文件前拒绝；已有输出不覆盖 | 同目录 `report.json` |
| 四项目锁定构建/旧探针冒烟 | 0 错误/0 警告；8 项冒烟 PASS（本轮未加 NX 采集） | `probe-20260903-154925-4225282/` |
| HTTP 本机限制 | 健康页 200；错误 Host 403；源码路径 404；非法 WS 请求 403 | `media-probe/http-20260903-154638-2434220.json` |

中途取消检查：最终版本在收到并解码 312 帧后点击停止，页面标明“用户停止；未记为通过”；随后 `/health/live` 返回 `busy=false`。服务 PID 13000 / 创建时间 15:49:45 未重启，之后再次开始并完成上表 60 秒编码。最终 HTTP 复测也为 4 PASS，证据为 `media-probe/http-20260903-155415-3475952.json`。

### 构建身份

十分钟实验运行期间补写了边界校验与测试，没有热替换正在运行的二进制。该实验属于修改前的构建；最终版本已单独短时重测，不能把十分钟记录归给任意后续版本。

十分钟实验二进制 SHA-256（替换前已读取）：

- Workbench.MediaProbe.dll：`73F5D360437C6B2DB77B61B9AC7218150349056CB6C08779998CEBB95620E3CE`
- Workbench.Windows.dll：`A10C86B0E05B2F47B251D99D15BEF3BF1B05253ED1A72675286D1243AC464BF0`

最终版本的浏览器报告自带 `buildIdentity`；CLI 回归报告有源码 SHA-256 清单。工程尚未提交，不杜撰 commit。首次两次失败（输出重新协商、错误消费第二个异步输出信用）留下 `hardware-01/02` 对应的未完成文件；它们没有成功报告，不能用于验收。

最终复测构建 SHA-256：MediaProbe `CF86E72F495C9D9AA5591B97D16CE6D8DF069B99DA632E0E756F87A6243B8CB7`；Windows `B7B91B1974F3F131EFFF5E29164B21036263895D3A50E7BFEA8E132F8E974521`。

## 3. 资源采样的有限结论

采样文件为 `media-probe/resources-20260903-154108-2153362.jsonl`。截取到十分钟编码结束时，有 94 个样本，范围 15:41:08–15:48:54，覆盖中后段约 466.6 秒，而不是全部十分钟。

- MediaProbe 进程工作集 152.69–155.00 MiB。
- 句柄数 737–767。
- 该段进程累计 CPU 时间增加 67.609375 秒；不是整机 CPU 百分比，也不包含浏览器。

这是 CPU 生成 NV12 的 720p 单路实验，不含 WGC 图形转换、真实 NX/TIA、双路和完整 Host/Agent。不能据此将 P09、P10 或 8 小时耐久标 PASS。

## 4. 本次解决的问题

Intel MFT 首次输入后返回 `MF_E_TRANSFORM_STREAM_CHANGE`。已根据实际输出媒体类型核对 H264 与尺寸并重新设置类型；异步编码器重新等待下一次 HaveOutput 事件，不能像同步编码器一样立即再取一次输出，否则实际返回 `E_UNEXPECTED`。

其他保护包括取消前置检查、有限事件轮询与十秒停滞超时、输出引用归属、独立 IDR 参数集、时间戳单调、profile 改变需新配置、输出队列限额、浏览器消息积压限额。正式媒体恢复尚未实现，探针超限明确失败，不跳过依赖帧冒充成功。

实现依据：[Microsoft 异步 MFT](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts)、[H264 编码器](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder)、[W3C AVC WebCodecs 注册规范](https://www.w3.org/TR/webcodecs-avc-codec-registration/)。

## 5. 复跑与下一步

```powershell
./scripts/verify-media.ps1
# 以下先按 tools/Workbench.MediaProbe/README.md 启动本机页面
./scripts/verify-media-http.ps1
```

仍待完成：WGC/D3D 真实窗口帧接入、GPU 色彩转换、真实 NX 动态/菜单/弹窗、完整输入、JPEG 对照、双路、1080p 指标与用户客户端。当前 MediaProbe 不是未来正式 Host 的替代品；原有 M1 NX 放行条件全部保留。
