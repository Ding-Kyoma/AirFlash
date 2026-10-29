# AirFlash × WinAirCast 改进进度

> **2026-10-29 更新**：你已经在 `QUESTIONS.md` 里答复了 Q1–Q26。答复里标
> `[已否决]` 的三条（Q8/Q12/Q21 关于运行中改 lead、Q26 的 QoS 路线）会推翻本节
> 的部分实现，**下一轮按答复返工**，清单见文末「按答复待返工」。其余条目
> （Q1/Q3/Q4/Q5/Q9/Q10/Q11/Q14/Q15/Q16/Q18–Q20/Q22/Q23/Q25）与我们已落地的做法
> 一致，只是数值口径的微调。

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
- 外环 `LatenessServo`（常数按 Q5 的答复改成字节级确认的值）：α=0.2 平滑、
  **engage > 5 ms / release ≤ 1 ms 的非对称迟滞**、`correction_ppb = filtered_ns / 20`
  （即 50 ppm/ms），**夹 ±50 ppm 且饱和静默计数**；没有积分项。
- 250 ms 断点按 Q5 的答复改为**整机重置**（filter/correction/engaged 全清零后
  重新预热），而不是「换个基线继续积分」——后者会把一次卡顿变成缓慢音高漂移。
- `DriftEstimator`：只观测不参与控制（1000 ppm 拒绝门限），避免两个控制器抢同一个
  执行器。
- **没有做**报告里的帧级 `interval_correction`（每 n 帧插删一帧，PCM 域、编码前）。
  现在靠重采样器做连续速率修正；按 Q6 的答复，插删在正常会话里「几乎不动」
  （50 ppm 时约 160 秒才动一帧），是否需要补上仍待评估。

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

**PTP 失步按 Q16 #3 改成「冻结」而不是拆会话**：`clock.rs` 新增
`timeline_frozen()`，交换停止后仍用最后一次 offset 继续映射，最长
`CLOCK_FREEZE_GRACE = 10 s`；进入/退出冻结各发一条 `clock_frozen` 警告
（`recovered` 区分）。超过宽限、或对端从未应答，才发 `clock_lost` 结束会话。
Q16 的原话是「off = 冻结 correction，不是计时拆会话」——10 秒这个数是我们自己
的耐心上限，不是 WAC 的。

**事件通道有限次重建（Q16 #2）**：多设备策略是「保留共享 RTP，除非媒体面失败」，
所以我们只在**事件通道**上重连：`Member` 记住 `event_addr` 与密钥，事件通道
降级后最多重建 3 次、每次间隔 2 秒，成功则 `clear_degraded()` 并发
`event_channel_rebuilt`（`recovered: true`），失败只发警告。音频链路全程不动。
Q16 #1 提到的「加密 RTSP 不能中途换密钥」对我们不适用：AF 用的是同一把
`Events-*` 密钥，重开连接不需要重新配对。

## P3-1 `mrCommand` 解析与执行（已完成）

`native/airflash-engine/src/events.rs`、`AirFlash.Core/RemoteCommand.cs`、
`AirFlash.App/Input/MediaKeys.cs`

事件通道的 body 过去被 `200 OK` acknowledgment 后直接丢弃。现在解析
`mrCommand` / `command` / `cmd` / `action`，同时接受字符串与 MRP 数字编码，
支持命令数组与可选 `volume`（0–1、0–100、dBFS 三种线上形式），以
`remote_command` 事件转给桌面端。无法识别的 body 返回空结果而不是报错，
事件通道不会因为一条怪消息掉线。

队列有界（32 条），超限丢最旧的并计数。

按 Q25 的答复补上**执行端**：`RemoteCommand` 把命令映射到媒体键
（`play/pause/toggle`→`VK_MEDIA_PLAY_PAUSE`、`next`→`VK_MEDIA_NEXT_TRACK`、
`previous`→`VK_MEDIA_PREV_TRACK`、`stop`→`VK_MEDIA_STOP`），由
`MediaKeys.Press()` 用 `SendInput` 注入；音量类命令没有对应媒体键，留给应用
自己处理。注入失败只记日志，不影响推流。

## P1-8 掩盖升级（已完成）

`continuity.rs` 的 `Splice`

原来欠载只有 5 ms 的 raised-cosine 交叉淡化，之后就是硬零：听感是「咔」一下
再无声。现在进入欠载时从最后一个样点做约 −52 dB / 10 ms 的指数衰减尾，
尾巴结束后才是精确静音（接收端的遮盖判断仍能认出全零帧）。
测试断言：尾巴期间幅度单调不上升、10 ms 内落到 1% 以下、随后是精确 0。

## P2-11 QoS 标记（已完成）

`native/airflash-engine/src/qos.rs`

音频 UDP socket 按 Q26 的答复走 qWAVE：进程级 `QOSCreateHandle`（句柄存在
`OnceLock` 里活到进程退出）+ `QOSAddSocketToFlow` + `QOSTrafficTypeVoice`，让
Wi-Fi AP 把音频放进 WMM 语音队列。QoS 服务被禁用或缺失时注册失败，只记遥测
（`MediaMetrics.qos_marked=false`），不影响会话。

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

- 引擎：`set_lead { ms }`（`session.rs::request_lead`）只改**发布映射里的锚点偏移**
  （`base_ns` 不可变，映射是 `base_ns + anchor_shift_ns() + frames`），**不发
  `SETRATEANCHORTIME`、不重置 RTP 原点**，所以不需要重建会话，也不会让对端跳段。
  上一版按 Q8/Q21 的答复修过这一点；上限不再是固定 2000 ms，而是**对端声明的
  `latency_ms + 100 ms`**（`lead_ceiling`，WAC 自适应范围的上界），`lead_changed`
  事件里带 `ceiling_ms`。人工设定的提前量不会被 `relax()` 回收。
- 桌面端：`AirFlash.Core/LatencyPolicy.cs` 按拓扑给出阶梯与已验证下限：

  | 拓扑 | 起点 | 下限 | 阶梯 |
  | --- | --- | --- | --- |
  | HomePod 单只 ALAC/PTP | 50 | 50 | 50/60/80/120/200 |
  | PCM/PTP | 60 | 45 | 45/60/80/120/200 |
  | ALAC/NTP | 200 | 120 | 120/160/200/300/500 |
  | 立体声对（原生或软件） | 120 | 120 | 120/140/160/200 |

- 新「自适应」模式（`LatencyMode = "adaptive"`）：默认仍是 `normal`，用户显式选择后
  才启用；设备级覆盖仍然优先。
- 上调：两条判据，任一满足就上一级，每分钟最多一级
  1. 一分钟内 3 次 `timeline_reanchored` / `sender_late_recovered`（突发性迟到）；
  2. **慢性 p99 迟到**（Q22 指出的盲区）：引擎每秒 `transport_metrics` 里的
     `lateness.p99_ms` 连续 10 次 ≥ 3 ms。这类流的延迟从不触及重锚预算，
     只看「恢复次数」永远发现不了。
- 下调：干净窗口后逐级回落，不低于下限。

## P3-2 连接前预判（按 Q18 的答复修正）

`profile.rs`

Q18 指出用 `features` **bit 41** 判 PTP 是错的（WAC 的二进制里 `1L<<41` 零命中）。
现在改成：

- **AirPlay 2 判据**：`features & bit40 != 0` **且** `srcvers >= 354.54.6`（与
  `SupportsAirPlay2` 一致），`bit41` 不再单独产生任何结论。
- **PTP 由型号 + 拓扑决定**，不是 feature bit：`ptp_expected()` 里 HomePod /
  `AudioAccessory` → PTP，Apple TV → NTP，未知型号 → 回到 AirPlay 2 判据。
  为此 `negotiate_with()` 多带 `/info` 的 `model` 与 `sourceVersion`；旧的四参
  `negotiate()` 保留给能力预测（那里没有型号信息，用 `inherited_ptp = false`
  只按对端自述预测）。
- 显式 `timing: ptp` 在「型号明确说 NTP」或「既无型号支持也无 AirPlay 2 证据」时
  直接报错，不再放行。

---

## P1-6 / P3-7 BMCA（按 Q17/Q27 的答复实现 SETUP 期协商）

`clock.rs`、`session.rs`

- `BmcaDataset`（`priority1`、`clockClass`、`accuracy`、`variance`、`priority2`、
  grandmaster identity、`stepsRemoved`）与 `parse_announce()`；快照里新增
  `bmca_priority1/clock_class/priority2/grandmaster/steps_removed` 与
  `better_master`，`clock_status` 每秒带出。
- **协商按 Q27 放在 SETUP 期**（`Member::connect` 里，`RECORD` 之前，此时没有
  timeline、没有音频，所以让位听不见）：
  1. PTP 启动后，仍在 master 角色时向接收端发 **Announce 订阅探测**，最多 3 次、
     间隔 300 ms；`negotiate()` 最多等 1.2 s。
  2. **判据只有 `priority1`** + `remote_is_master`（Announce 带有效 grandmaster
     identity），与 WAC 的「Mac-style negotiation」一致；`priority2`
     （WAC 二进制里 0 命中）和 `stepsRemoved`（解析但不参与决定）都不作为比较键。
  3. 对端 `priority1` 更低 → `yield_to()`：转为 slave，之后只发 `Delay_Req`、
     订阅 `Sync/Follow_Up`，不再发 Announce。否则（对端更差、相等、或压根不
     应答）**保持自己是 master**（fail-open）。
  4. 每次会话发一条 `bmca_verdict` 事件（`yielded`/`our_p1`/`remote_p1`/
     `remote_is_master`/`probes`），便于从日志判断为什么当了 master。
- **代价**：master 路径每次都多花最多约 1.2 s 启动探测（对端不应答时），与 WAC
  的「probe → re-probing → giving up」同构。
- **没验证的部分**：真实 HomePod 是否应答 Announce、以及它的 `priority1` 具体值，
  都没有设备实测；如果它始终不应答，行为等价于改造前（保持 master）。

---

## 还没做

| 项目 | 现状 | 备注 |
| --- | --- | --- |
| P1-10 标定流程 | 未开始 | 需要真机测延迟/丢包，按报告是「用户自助标定」 |
| P2-12 组拓扑原子化 + 失败回滚 | 未开始 | 屏障已有（全部 connect 后才等 PTP）；缺的是中途失败时 `TEARDOWN` 已建好的成员并重试 |
| P2-13 兼容 profile | 未开始 | 把散落的 if 收成 `CompatBehavior`，按 `srcvers`/`model` 指纹选择 |
| P2-14 诊断导出 | 部分 | `diag_summary` + `--self-check` 已能给结构化引擎摘要；缺脱敏与实际导出文件 |
| P3-3 抢占检测 | 未开始 | 按 Q18 用「未预期 TEARDOWN + 随后重新 announce」组合特征 |
| P3-4 MRP 隧道 | 未开始 | 大工程 |
| P3-5 经典 RAOP / 初代 AirPlay | 未开始 | 大工程 |
| P0-4 封面（`image/jpeg`） | 未做 | 尺寸/格式约束需要设备确认，见 Q10；失败也不能干扰音频 |
| P0-3 帧级 `interval_correction` | 未做 | 见上文，现用连续重采样代替 |
| 采集进程退出后自动重开 | 部分 | 现在引擎结束会话、由桌面端重连；进程内自愈未做 |
| 音量事件（音箱旋钮/遥控器） | 部分 | 事件里的 `volume` 已解析并转发，但还没写回设备音量状态 |

## 按答复待返工（下一轮）

按 `QUESTIONS.md` 的答复，下面几处已经落地的实现需要改，标 **[否决]** 的是
方向性错误：

| 项 | 现在 | 按答复应改成 |
| --- | --- | --- |
| ~~P0-2 运行中改 lead（Q8/Q21）~~ **已按答复修正** | `set_lead` 只改 sync 锚点偏移，`base_ns` 保持不可变（`base_ns + anchor_shift_ns() + frames`），不发 `SETRATEANCHORTIME` | 上限改为对端声明的 `latency_ms + 100 ms`（`lead_ceiling`），超出被夹住并在 `lead_changed` 里报 `ceiling_ms` |
| ~~P2-11 QoS（Q26）~~ **已按答复改成 qWAVE** | 进程级 `QOSCreateHandle`（`OnceLock` 持有到退出）+ `QOSAddSocketToFlow` + `QOSTrafficTypeVoice`；服务不可用时返回 false，只记 `qos_marked` |
| **[否决]** P0-4 SMTC 策略（Q12） | 2 s 轮询 + 300 ms 稳定窗 | 按答复改成事件驱动（SMTC 的 `MediaPropertiesChanged`/`TimelinePropertiesChanged`），进度策略按答复的阈值 |
| ~~P0-3 伺服常数（Q5/Q6）~~ **已按答复修正** | 外层改为 `correction_ppb = filtered_ns / 20`（50 ppm/ms），夹 ±50 ppm，**无积分项**；engage >5 ms / release ≤1 ms 非对称迟滞；250 ms 断点改为**整机重置**（filter/correction/engaged 全清零再预热）；饱和静默计数 |
| P3-1 遥控命令（Q25） | 解析 RTSP 事件里的 `mrCommand` | 同时接受 `SendInput` + `VK_MEDIA_*` 的输入事件路径 |
| P1-9 事件通道（Q16/Q25） | EOF 后整条会话重启 | 按答复的重建策略细化（不重建音频链路） |

数值微调但方向已确认的：Q2（budget 用「该发多少包 vs 实际发了多少包」折算，
不是裸毫秒常数）、Q7（漂移上限口径）、Q13（`DATA_DISCONTINUITY` 判伪的额外条件）、
Q24（同上）。
