# Windows 原生能力探针

仅供被控机本地诊断。不是正式远控服务，没有网络监听、键鼠注入或软件关闭操作。

在项目根目录 PowerShell 执行：

```powershell
. .\scripts\environment.ps1
& $Dotnet build Workbench.slnx -c Debug
& $Dotnet run --project tools/Workbench.Probe --no-build -- --help
& $Dotnet run --project tools/Workbench.Probe --no-build -- --displays
& $Dotnet run --project tools/Workbench.Probe --no-build -- --encoders
& $Dotnet run --project tools/Workbench.Probe --no-build -- --encode-test --frames 90
& $Dotnet run --project tools/Workbench.Probe --no-build -- --encode-test --software --frames 90
& $Dotnet run --project tools/Workbench.Probe --no-build -- --list
& $Dotnet run --project tools/Workbench.Probe --no-build -- --children
& $Dotnet run --project tools/Workbench.Probe --no-build -- --seconds 3
```

- 默认进程名 `ugraf`，可用 `--process` 选择测试夹具或实际 TIA 进程名。
- 多窗口时先 `--list`，再将返回的十进制 handle 作为 `--window` 参数。它只能选择匹配进程枚举得到的可见顶层窗口。不要把这个本机诊断参数暴露为网页 API。
- 最小化目标不会自动恢复，明确报错；恢复窗口后再运行。
- `--children` 默认过滤隐藏子窗口；诊断布局可加 `--include-hidden`。
- `--report <新文件.json>` 保存任一模式的报告；`--output <新文件.png>` 保存采集截图。采集默认同时保存同名 JSON。已有输出拒绝覆盖，方便保留失败和历史证据。
- Ctrl+C 取消采集。测试时间为首帧快照之后的观察时间，不是视频帧率基准。

## 证据边界

可复跑冒烟入口为 `scripts/verify-probe.ps1`；加 `-CaptureNx` 会额外采集已打开、未最小化的真实 NX 第一帧，不注入输入。报告和构建日志进入每次新建的 artifacts/verification/probe-* 目录。冒烟通过不等于 M1 或业务验收通过。

`--encoders` 枚举并激活 H264 MFT，不配置媒体类型、不编码帧。硬件标记来自硬件类别枚举，不能仅凭激活成功宣称持续硬编已通过。多个同名编码器可能对应不同枚举实例，尚未选择/验证具体设备。

`--encode-test` 则真实编码生成的 NV12 动态测试图：默认第一候选硬件 MFT，`--software` 明确选择同步软件候选，不自动降级。输出 `.h264` 与每个 access unit 的偏移、长度、时间戳、NAL 类型报告；1–18000 帧，按 30 FPS 节拍送入。拒绝覆盖，Ctrl+C 可取消；失败可能留下未完成的码流，只有成功的配套 JSON 才包含完整结果。它不使用或修改任何 NX 模型。可复跑 `scripts/verify-media.ps1`；浏览器实时解码验证工具见 ../Workbench.MediaProbe/README.md。

截图模式按 HWND 使用 WGC，不通过屏幕矩形代替。它只保存第一帧；静止源可能仅产生这一帧。`frameArrivalRate` 是观察期的帧到达率，不是浏览器呈现帧率，`firstFrameMs` 也不是输入到可见响应延迟。此探针尚不证明动态、弹窗合成、尺寸恢复或双应用输入。

截图和窗口标题可能包含工程信息，只留在本地 artifacts，默认不提交、不上传。正式验收见 docs/ACCEPTANCE.md。

## 真实 GPU 视频与关联窗口实验

先 `--process ugraf --list` 取得当时有效的根窗口句柄，然后在本地执行：

```powershell
& $Dotnet run --project tools/Workbench.Probe --no-build -- --encode-window --process ugraf --window <当前句柄> --seconds 3 --output artifacts/verification/new-window.h264
& $Dotnet run --project tools/Workbench.Probe --no-build -- --encode-window --process ugraf --window <当前句柄> --owned --seconds 3 --output artifacts/verification/new-scene.h264
```

请为每次实验使用新的文件名。`--owned` 只允许与 `--encode-window` 一起使用。该模式将根窗口及同进程 owner 链上的可见弹窗进行 GPU 预乘 Alpha 合成，再转 NV12 硬编；节点数量/纹理面积有硬上限，无整桌面回退、无键鼠注入。报告保存原生节点、实际边界、场景版本和每个编码 access unit 的捕获版本。跨进程辅助窗口、完整恢复和真实输入仍待实现。浏览器同流验证使用 `Workbench.MediaProbe --owned`，详见其 README。
