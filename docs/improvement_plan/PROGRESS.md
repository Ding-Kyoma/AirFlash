# AirFlash × WinAirCast 改进进度

对照 `REPORT-WinAirCast-vs-AirFlash.md` 的优先级清单逐项推进。本文件只记录
「已经落地并跑过验证」的内容、判定依据，以及还没做的部分，避免把设计意图
写成既成事实。

验证口径（每次提交都在开发分支跑过）：

| 命令 | 结果 |
| --- | --- |
| `cargo test --manifest-path native/airflash-engine/Cargo.toml --locked` | 123 lib + 各集成测试全部通过 |
| `cargo clippy --all-targets -- -D warnings` | 无告警 |
| `pwsh scripts/build-native.ps1 -Check` | 通过（含 release 构建与兼容性审计） |
| `pwsh scripts/dotnet.ps1 test AirFlash.sln` | 435 通过 |
| `uv run pytest` / `uv run ruff check .` | 141 通过 / 无问题 |

---

## P0-5 会话遥测（已完成）

`native/airflash-engine/src/telemetry.rs`

- 每个会话一个 epoch，统计发包数、准点/迟到、补静音、重锚、超期、欠载
  （区分「有音乐时欠载」与「空闲保活欠载」）、采集丢帧、解码丢弃、采集故障。
- 延迟、采集样本年龄、重传缺口用 64 桶对数直方图（0.05–200 ms），
  `p95/p99/最大值` 直接进 `transport_metrics`，不再靠猜。
- 新增 `diag_summary` IPC 命令与 `diagnostics` 能力标记，桌面端可以一次拿到
  「引擎版本 + 会话摘要」，用于工单和自检。

**为什么不只加日志**：报告指出 WinAirCast 的问题是「有数据但没人看」。直方图
与分类计数让「偶发一次卡顿」和「每 30 秒卡一次」在指标上可区分。

## P0-1 时间线重锚（已完成，优先于 P0-3）

`native/airflash-engine/src/transport.rs`、`session.rs`

原行为：迟到超过 8 个包（约 40 ms 静音）就返回 `timeline_expired`，桌面端重建整
个会话。用户听到的是「断一下 + 重新连接」。

新行为：把**整条时间线的起点**后移（`MediaSchedule.shift`），`seq` 与 `rtptime`
保持线性，只移动 `rtptime → networkTime` 的锚点，并给 1 帧相位余量。
预算 10 秒内 3 次、单次上限 2000 ms；超出预算才真正 `timeline_expired`。
多出来的提前量按每秒 1 帧慢慢收回，避免延迟永久抬升。

- 测试：`stalled_sender_reanchors_instead_of_rebuilding_and_keeps_seq_linear`
  用真实 UDP 接收端制造两次 300 ms 停顿，断言 `seq` 连续、`rtptime` 步进 352、
  `skipped_packets == 0`、`reanchored >= 1`。
- 拒绝的方案：跳过若干 `seq`（会在接收端产生重复 nonce，且破坏「seq 连续」验收）。

## P0-3 迟到伺服与漂移观测（已完成）

`native/airflash-engine/src/servo.rs`（10 个单测）

- 内环 `QueueRegulator`：对采集队列深度做 PI 控制，带抗饱和，限幅 ±400 ppm。
  取代原来的 `(target-have)/rate*0.01` 纯比例式——那个公式在恒定采集时钟偏差下
  会稳定停在几十毫秒的固定误差上。
- 外环 `LatenessServo`：α=0.2 平滑、250 ms 步长、2 ms 死区、5 ms 进入 / 2 ms 退出，
  kp=20 ppm/ms、ki=25 ppm/(ms·s)，上限 300 ppm。
- `DriftEstimator`：只观测不参与控制（1000 ppm 拒绝门限），避免两个控制器抢同一个
  执行器。
- 单个大停顿按「阶跃」处理：重置积分项，不让它变成缓慢的音高变化。

## P1-7 采集加固（部分完成）

- **伪 `DATA_DISCONTINUITY`**：WASAPI 在回环缓冲绕回时也会置位该标志。现在用设备
  位置连续性（`position == 上一包结束位置`）判真伪，伪标志只计数不冲洗状态，
  不再白丢 20–60 ms 音频。
- **TPDF 抖动**：16 bit 输出带三角抖动，安静段不再有量化相关的「沙粒感」；
  数字静音仍保持精确 0，因为接收端的气象掩盖逻辑依赖全零帧。
- 真实断流仍走原来的「清队列 + 重置重采样器 + 打 splice 边界」。

## P1-9 控制面健康策略（已完成）

`transport.rs` 的 `Health`

反馈/事件通道故障过去直接 `fail()` 整个会话。现在分两级：

- `degrade()`：控制面故障，记为 `control_degraded`，媒体仍在送达就继续播，
  并通过 `warning` 上报（`feedback_lost`）。
- `fail()`：认证失败、会话被拒（401/403/454），重连是唯一解，立即失败。

会话循环每秒用 `check_control(media_healthy)` 判定：只有「媒体也停了」才把降级
升级为终止。窗口 2 秒。

## P3-1 `mrCommand` 解析（已完成）

`native/airflash-engine/src/events.rs`

事件通道的 body 过去被 `200 OK`  acknowledgment 后直接丢弃。现在解析
`mrCommand` / `command` / `cmd` / `action`，同时接受字符串与 MRP 数字编码，
支持命令数组与可选 `volume`（0–1、0–100、dBFS 三种线上形式），以
`remote_command` 事件转给桌面端。无法识别的 body 返回空结果而不是报错，
事件通道不会因为一条怪消息掉线。

队列有界（32 条），超限丢最旧的并计数。

## P1-8 掩盖升级（已完成）

`continuity.rs` 的 `Splice`

原来欠载只有 5 ms 的 raised-cosine 交叉淡化，之后就是硬零：听感是「咔」一下
再无声。现在进入欠载时从最后一个样点做约 −52 dB / 10 ms 的指数衰减尾，
尾巴结束后才是精确静音（接收端的遮盖判断仍能认出全零帧）。
测试断言：尾巴期间幅度单调不上升、10 ms 内落到 1% 以下、随后是精确 0。

## P2-11 QoS 标记（已完成）

`native/airflash-engine/src/qos.rs`

音频 UDP socket 打 DSCP 46（EF），让 Wi-Fi AP 把音频放进 WMM 语音队列。
用 `setsockopt(IPPROTO_IP, IP_TOS)` 做，best-effort：失败只记遥测
（`MediaMetrics.qos_marked`），不影响会话。qWAVE（`QOSAddSocketToFlow`）留作
后续，见 Q26。

## P0-4 元数据（引擎与桌面已打通，封面未做）

`native/airflash-engine/src/metadata.rs`、`AirFlash.Core/NowPlaying.cs`

- IPC `set_now_playing { title, artist, album, genre, duration_ms, position_ms, playing }`。
- 引擎侧 worker 在控制连接上依次发送：
  1. `SET_PARAMETER` + `text/parameters`：`progress: <start>/<position>/<duration>`（毫秒）；
  2. `SET_PARAMETER` + `application/x-dmap-tagged`：`mlit` 里的 `minm`/`asar`/`asal`/`asgn`。
- 节流：同一曲目进度只在 ≥1500 ms 变化时重发；标签变化才重发 DMAP；
  标签没变时不会重复发第二遍（`tags_sent`）。
- 桌面侧 `NowPlayingPump`：2 s 轮询 + 300 ms 稳定窗，去抖后只在
  标签/播放状态/时长变化或进度变化 ≥1500 ms 时下发。
- 来源：`AirFlash.App/Metadata/SmtcNowPlayingSource.cs` 读 Windows SMTC。
  为此把 App 的 TFM 提到 `net10.0-windows10.0.19041.0`（`net10.0-windows` 没有
  WinRT 投影，拿不到 `Windows.Media.Control`）；SMTC 不可用时返回 null，
  只保留上一次显示，不影响推流。
- 没做：封面（`image/jpeg`）。尺寸/格式约束无法离线验证，且失败不能干扰音频。

## P0-2 自适应提前量（已完成）

- 引擎：`set_lead { ms }`（`session.rs::request_lead`）把请求值作用到
  `MediaSchedule.shift`，同步锚点跟着移动，因此**不需要重建会话**。上限 2000 ms；
  人工设定的提前量不会被 `relax()` 回收。
- 桌面端：`AirFlash.Core/LatencyPolicy.cs` 按拓扑给出阶梯与已验证下限：

  | 拓扑 | 起点 | 下限 | 阶梯 |
  | --- | --- | --- | --- |
  | HomePod 单只 ALAC/PTP | 50 | 50 | 50/60/80/120/200 |
  | PCM/PTP | 60 | 45 | 45/60/80/120/200 |
  | ALAC/NTP | 200 | 120 | 120/160/200/300/500 |
  | 立体声对（原生或软件） | 120 | 120 | 120/140/160/200 |

- 新「自适应」模式（`LatencyMode = "adaptive"`）：默认仍是 `normal`，用户显式选择后
  才启用；设备级覆盖仍然优先。
- 上调：一分钟内出现 3 次 `timeline_reanchored` / `sender_late_recovered` 就上一级，
  每分钟最多一级；下调：干净窗口后逐级回落，不低于下限。

---

## 还没做

| 项目 | 现状 | 备注 |
| --- | --- | --- |
| P0-4 封面（`image/jpeg`） | 未做 | 尺寸/格式约束需要设备确认，见 Q10；失败也不能干扰音频 |
| P1-6 / P3-7 BMCA | 未开始 | 只在多 HomePod 时才有意义，需要真实设备验证 |
| P3-2 TXT 预测（bit40 + `srcvers`） | 未开始 | 发现逻辑在 C#，需要确认 HomePod 上报的 TXT 字段 |
| P3-3 抢占检测 | 未开始 | 依赖元数据事件 |
| 采集进程退出后自动重开 | 部分 | 现在引擎结束会话、由桌面端重连；进程内自愈未做 |
| 音量事件（音箱旋钮/遥控器） | 部分 | 事件里的 `volume` 已解析并转发，但还没写回设备音量状态 |
