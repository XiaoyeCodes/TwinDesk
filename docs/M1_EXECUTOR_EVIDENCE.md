# M1-02 有界执行器与捕获绑定

2026-09-04，从 main/bdb7850 干净工作树续接。以下为增量证据，不是 M1 放行或全项目完成。

## 实现范围

- `BoundedInputExecutor` 独占一个 InputSession/原生后端，独立线程串行处理输入、几何、发送/显示确认和心跳。队列默认 256 项；排队达到 500ms 后失效并撤销，避免迟到编辑。暂不合并 move；离散事件超限清队列后释放，不静默丢 keyup。
- 25ms 空闲 tick 不依赖媒体；本地撤销立即关闭准入。在途原生调用结束前不并发释放。停止超时返回 Completed=false/Released=false，不授权另起输入者。真正原生卡死仍需要 M2/M6 Host/Agent 故障隔离与退出确认，当前线程不能保证卡死调用 6 秒内恢复。
- 释放失败保留账本，最多再尝试 3 次，仅抬键；安全停止和线程停止分别报告。日志不输出文本正文。
- `CaptureInputBindings` 由真实 WGC 节点创建，首帧与完整合成后才发布不可变几何。Closed/Dispose 立即撤销绑定；同 HWND 新绑定采用递增代次，旧对象的迟到关闭不会污染新绑定。根捕获 Closed 必须重新建立流/应用生命周期，不能当作旧根继续。
- 单独绑定不能替代浏览器已显示确认，WindowsInputEnvironment 仍需即时进程/桌面/权限/原生命中检查。全局输入检查与实际注入的竞争窗口仍存在，不宣称 Windows 桌面安全隔离。

## 实际执行

| 命令 | 证据 | 实际结果及范围 |
| --- | --- | --- |
| `scripts/verify-input.ps1` | `artifacts/verification/input-core-20260904-095048-3842451/` | 85 项通过，含新执行器 13 项、新绑定 6 项；L0 故障后端，不调用 SendInput |
| `scripts/verify-scene.ps1 -Cycles 40` | `artifacts/verification/scene-fixture-20260904-095052-8873701/` | 87 项通过；真实自身 WGC/GPU 每个合成检查均核对 liveInputBindingsVerified，源释放后活动捕获为 0；不含键鼠/浏览器/NX/TIA |

报告记录源码 SHA-256；场景报告含二进制身份。旧 `input-core-20260904-094922-5493517` 为执行器单独加入后的 79 项，不覆盖后续绑定增量。

## 下一步

原生增量已执行：`artifacts/verification/native-input-20260904-095230-7484699/` 共 19 项 PASS，范围为有界执行器 + 真实 WGC 生命周期绑定 + 自身窗口 SendInput。心跳释放观察值 5495.2784ms，结束 heldCount=0、pendingUnicode=false。Computer Use 只点击自测启动按钮；最初仅文本观察的点击报 geometry unavailable，重新截图后一次点击启动成功，没有操作 NX/TIA。显示确认仍为自测显式模拟，不是浏览器。全量回归 `media-20260904-095327-5818636` 为 127 项 C#/5 项 JS 及硬软编码各90帧；构建/冒烟 `probe-20260904-095337-7536747` 为五项目0警告/错误和8项检查。

上述原生接入已完成；随后 F0 回环浏览器也取得局部实际证据，见 [F0浏览器输入](./M1_BROWSER_INPUT_EVIDENCE.md)。最新全量回归为130 C#/13 JS，最终场景87项。下一项是仍未验证的 SC04 NX 参数框；SC05–SC08 不因此通过。随后补真实 NX 键鼠/编辑、双窗/JPEG、720p/1080p 真实动态连续实验。TIA、第二客户端、公网和8小时要求不减少。
