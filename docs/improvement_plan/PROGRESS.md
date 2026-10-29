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
| `cargo test --manifest-path native/airflash-engine/Cargo.toml --locked` | 152 lib + 26 集成通过（2 个硬件/长稳用例仍 ignore） |
| `cargo clippy --all-targets -- -D warnings` | 无告警 |
| `pwsh scripts/build-native.ps1 -Check` | 通过（含 release 构建与兼容性审计） |
| `pwsh scripts/dotnet.ps1 test AirFlash.sln` | 457 通过 |
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
  并通过 `warning` 上报。**运行中的 `/feedback` 被拒（任何非 200，含
  401/403/454）属于这一级**：只发一条 `feedback_rejected` warning 并降级，
  不再 `fail()`、不产生抢占证据、不写 `ended_at`；Q34 的 WAC 原文是
  `feedback failure is not media failure`。
- `fail()`：只留给 SETUP 阶段（含 SETUP 前的 `/info`）的 401/403/454 和真正的
  认证/加密失败——此时还没有媒体，重连才是唯一解，`authentication_failed`
  仍不可重试。运行中的 `/feedback` 拒绝永远不走这条路（Q16 边界不变）。

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

## P2-14 诊断导出（已完成脱敏与导出）

`native/airflash-engine/src/telemetry.rs`、`AirFlash.Core/DiagnosticExport.cs`、
设置页「导出诊断信息」按钮

- 引擎 `telemetry::redact()`：按 key（`host`/`ip`/`mac`/`path`/`receiver_id` 等）
  与自由文本里的 IPv4/MAC 一起替换成**稳定假名**（`host-1`、`host-2`…，同一地址
  全篇同名），`secret`/`password`/`key`/`authorization`/`token` 直接 `[redacted]`；
  所有计数器、直方图、版本号原样保留。`diag_summary` 现在返回脱敏后的快照。
- 桌面 `DiagnosticExport`：向引擎要一份脱敏快照（没有引擎时返回 null 而不是抛错），
  拼上 app/engine 版本、OS、CPU 数、日志尾部 80 行，写到日志目录的
  `AirFlash-diagnostics-<时间戳>.json`，并打开该目录。
- 测试：脱敏（同一主机同一假名、自由文本里的 IP 也被替换、数字不变）、快照往返、
  无引擎不抛错、文件命名。

## P2-12 组拓扑原子化（已完成）

`session.rs`

- **原子性本来就有**：`Member` 的 `Drop` 会调 `close()`，`session_started` 为真时
  发 `TEARDOWN`，所以第 N 个成员失败时，前 N-1 个随 `Vec` 一起被拆掉，接收端
  不会看到「半配好的立体声对」。
- 本轮补的是**重试**：每个成员最多重试 1 次（间隔 400 ms），失败时先发
  `member_setup_retry` 警告再重连；仍然失败才把 `setup` 错误上抛，由上面的
  RAII 负责回滚。

## P3-8 捕获自愈（已完成）

`native/airflash-engine/src/live.rs`、`applications.rs`、`session.rs`

- **重开端点而不是结束会话**：绑定抽成可重跑的 `bind_target()`。应用进程退出、
  端点失效（`AUDCLNT_E_DEVICE_INVALIDATED`）、读写错误、或判断端点不再产出时，
  丢掉旧 COM 对象、重新绑定，并在同一个采集线程继续。连续 3 次重绑失败才把
  `ended` 置真，交回 `run_live` 重开端点或进入 standby——与改造前「结束会话、
  桌面重连」的最终行为一致，只是把恢复提前到了进程内。
- **重绑不丢已编码音频**：只清空重采样器的输入队列与 `input_times`、`reset()`
  重采样器，`state.frames` 原样保留；`boundary_pending` 置位并累加
  `discontinuities`。真正的 WASAPI 断流仍按 P1-7 的「清队列」处理，两档不混。
- **多实例**：应用目标按可执行文件路径抓取所有根实例（上限 4），解码后按时间戳
  网格在采样级相加，再进同一个重采样器。IPC 请求带 pid+started_at 时，只要该实例
  还活着就只抓它；它退出后才回到按路径抓取。路径不同的进程永不附加；同 pid、
  不同 `started_at`（PID 复用）不会被当成原实例。
- **无声检测只做探测**：只有本会话已经发生过真断点或成功重绑，连续 ≥2 秒全零 /
  `SILENT` 才允许探测；每 60 秒最多一次，非静音音频一回来立即恢复探测额度。
  只是还没开始播放（从未断过）不会触发。无包超过 2 秒仍只做原来的
  `GetNextPacketSize` 存活检查，出错才按 `endpoint_lost` 重绑。
- **指标**：`capture_metrics` 新增 `rebinds`、`rebind_failures`、
  `capture_instances`、`silent_probes`、`last_rebind_reason`；全部带 serde
  默认值，旧桌面端忽略新键即可。
- **警告**：`capture_process_exited` / `capture_endpoint_reopened`
  （`recovered:true`）与 `capture_rebind_failed` / `capture_rebind_exhausted`
  （`recovered:false`），由 `session.rs` 用现有 `warning` 形状发出，只在状态
  变化时发，不在每个包上发。
- 测试：重绑预算（连续失败耗尽、成功清零）、静音探测（2 秒门限、60 秒限速、
  无断点不探测、非静音恢复额度）、实例选择（pinned / 全路径 / PID 复用 / 上限）、
  状态级重绑保留 `state.frames`、混音网格与静音补洞、设备失效分类。
- **没做/没验证**：没有真机，也没有两个应用同时发声的离线环境，跨进程 QPC 基准
  是否逐样本对齐只能按「同一引擎时钟」假设（见 Q28）；没有做
  `IAudioEndpointVolume` 静音识别，「用户把系统音量拉到 0」仍可能被当成静音，
  靠 60 秒限速兜底；没有改 RTP/RTSP/编码路径。进程退出靠 50 ms 轮询 + 250 ms
  退避发现，未做事件驱动的退出通知。

## P2-13 兼容 profile（已完成）

`native/airflash-engine/src/profile.rs`、`src/session.rs`、`src/receiver_capabilities.rs`、
`tests/compat_profile.rs`

- **一个指纹只解析一次**：新增 `CompatFingerprint`，把 `/info` 的
  `supportedFormats`/`supportedAudioFormatsExtended`、`features`、`sourceVersion`、
  `model` 以及桌面端下发的 legacy codec 列表收进一个值。`/info` 的 feature 字
  优先，缺失时才用 mDNS/桌面端下发的 `profile.features`；`airplay2()`（bit40 且
  `srcvers >= 354.54.6`，bit41 不参与）与 `ptp_expected()`（按型号）是仅有的两个
  原始结论，调用方不再自己读 bit 或型号字符串。
- **一个协商结果**：`CompatProfile` = 指纹 + 本次会话的 codec/transport/timing +
  follow 标记，对外只回答具名问题：`prefers_alac()`、`uses_buffered_stream()`、
  `uses_ptp()`、`follows_receiver_clock()`、`clock_role()`、`timing_label()`、
  `timing_protocol()`、`codec_name()`、`transport()`。`Member` 里原来的
  `selection: Selection` 换成 `compat: CompatProfile`；`session.rs` 里 5 处
  `transport == Buffered` 比较、15 处 `selection.ptp` 判断（含 `negotiated`/
  `session_metrics` 两个事件里的 `clock_role`/`timing` 三元表达式）和 2 处
  `selection.alac` 全部改成问同一个值，`selection.ptp && ptp.is_none()` 等启动顺序不变。
- **`negotiate_with` 收掉**：型号/固件进了指纹后，原来 7 个参数的
  `Formats::negotiate_with()` 和专属的 `#[allow(clippy::too_many_arguments)]` 一起删除，
  换成 `CompatFingerprint::negotiate(profile, rate, inherited_ptp)`；`Formats::negotiate()`
  只保留为「没有型号证据」的薄封装。`receiver_capabilities::choices()` 也走同一段
  协商代码，预判与运行时不再各写一份 if。
- **没引入报告里的哪些行为，以及为什么**（报告那一行是 WAC 的行为清单，AF 没有对应分支）：
  - `LegacySyncPacketLatency`：AF 只有一套 Sync/Follow_Up 估计器，没有「旧固件用
    不同同步包延迟」的分支；造这个变体等于凭空给某些型号加时钟行为，离线无法验证。
  - `SkipFlush`：AF 不处理 FLUSH，也从不发送 FLUSH（`session_simulation.rs` 里还有
    「会话中不得出现 FLUSH」的断言），没有可收拢的兼容分支；Q18 的答复也确认不需要
    为 FLUSH 加兼容分支。
  - `NoPeerDelay`：AF 实现了 PTP peer delay（`clock.rs` 的 `pdelay_response` /
    `pdelay_follow_up`），但没有按型号或 `srcvers` 关掉它的分支；哪些设备不该收
    peer delay 没有离线证据，不能猜。
  - `BplistSetup`：AF 的 SETUP 一直就是 binary plist（`rtsp.rs::plist_request` 只写
    `application/x-apple-binary-plist`），不存在「旧设备换一种 SETUP 编码」的第二条路。
  因此 `CompatProfile` 只收 AF 真实存在的判定（codec/transport/timing/follow），
  没有为不存在的分支造 enum 变体，也没有新增任何设备特例。
- **行为不变性**：新增 `tests/compat_profile.rs`，一张 97 行的显式表——64 行指纹矩阵
  （`HomePod1,1`/`AudioAccessory5,1`/`AppleTV6,2`/未知 × features 缺省/0/bit40/
  bit40|bit41 × `srcvers` 缺省/354.54.5/354.54.6/366.0，legacy codec 列表轮换）
  加 33 行 transport/codec/timing/legacy 决策行，逐行断言 `/info` 指纹 →
  codec/transport/timing。这张表在重构前（旧 `negotiate_with` 路径）先全绿，
  重构后再次全绿；P2-13b 只在这个文件尾部追加组决策测试，97 行本身一行没改。
- **没改的**：`receiver_capabilities` 的能力预判仍只用 `supportedFormats` + 对端自述
  `features`，不带 `model`/`sourceVersion`；P2-13b 后预判改走与运行时同一个
  `select_timing`（单成员 `TimingPlan`），但输入不变，所以 IPC 里
  `selected_timing` 的取值与改造前完全一致。

## P3-3 抢占识别（WAC 模型 + `statusFlags` 位定义 + Q32 死锁修复 + Q34 busy/存活，已完成）

`native/airflash-engine/src/preemption.rs`（重写）、`rtsp.rs`、`transport.rs`、
`session.rs`、桌面状态与文案

- **为什么返工**：上一版把「未预期 TEARDOWN / RTSP 403 / `Connection: close`」
  当信号，再要求媒体停流或 PTP association 改变作旁证，才判 `preempted`；
  没有旁证就退回通用故障。Q30 的答复指出这个方向反了：WAC 的权威判据是
  **live `GET /info` 的 `statusFlags`**，控制面动作只是带上下文的证据，而且
  **探测失败/超时反而照样回收**（fail-open）。原实现会把「接收器已被别人占用、
  但本地媒体尚未停流」误判成普通网络故障，也会在探测不到时错过回收时机。
- **唯一判据是 live probe**：`ProbeOutcome { Free, OursActive, BusyElsewhere,
  Unknown }`，`probe()` / `probe_report()` 在**新开的 RTSP 连接**上发
  `GET /info`，复用 `rtsp::Connection` 与二进制 plist 解析，不引入第二个 HTTP
  客户端；默认超时 2 s，超时、连接失败、RTSP 错误、plist 错误都返回 `Err`。
  控制面只有未预期 `TEARDOWN` 和 `Connection: close` 两条线索会触发探测；
  运行中 `/feedback` 的 `403`（以及任何拒绝状态）按 Q34 只降级控制通道，见下。
- **位定义已落定（Q31 答复）**：不再保留 `ANOTHER_SENDER_BIT = None`，改成三个
  64 位常量，命名与 WAC 相同：`RECEIVER_SESSION_ACTIVE = 0x20000`（131072，
  bit17，`ReceiverStatusFlags.ReceiverSessionActive`）、
  `STEREO_PARTNER_UNREACHABLE = 0x4000`（16384，bit14）、
  `PASSWORD_REQUIRED = 0x80`（128，bit7，
  `ReceiverAccessPolicy.PasswordRequiredFlag`）。`statusFlags` 按 WAC 的
  `ulong` 当 **`u64`** 解析：旧实现只取低 32 位，现在高于 `u32::MAX` 的位图也
  完整参与判定，不再把高位值当 `Free`/`Unknown`。bit14 和 bit7 **不参与判定**：
  单测把两位与 bit17 交叉组合，断言判决不变；bit14 只在 probe 日志里作为遥测
  （`stereo_partner_unreachable`）出现。
- **三分类（RTP 不健康时的回退）**（`classify(status_flags, we_hold_session)`）：
  | `statusFlags & 0x20000` | `we_hold_session` | 判定 | 动作 |
  | --- | --- | --- | --- |
  | 0 | 任意 | `Free` | 二次确认后回收 |
  | 置位 | `true` | `OursActive` | **不回收**：`FallThrough`，走原来的非抢占路径 |
  | 置位 | `false` | `BusyElsewhere` | 二次确认后进入退避队列（3/6/12/24/60 s，封顶 60 s），**永不放弃** |
  | 缺字段/类型错 | 任意 | `Unknown` | 立即回收（fail-open） |
- **Q34 的第四维：TCP 存活探测**：`statusFlags` 只说明「接收器上有没有会话」，
  不说明「那台设备还在不在」。新增 `ReceiverLiveness { Alive, Unknown, Dead }`，
  由 `receiver_liveness(ip, port, 2s)` 对**控制端口**做一次裸
  `TcpStream::connect_timeout`：接通即 `Alive`（拿到 socket 立刻丢弃，不发任何
  RTSP 请求）；连接被拒或超时 ⇒ `Dead`；其他错误 ⇒ `Unknown`。`combine` 把它和
  媒体健康组合：
  | `media_healthy` | `ReceiverLiveness` | 判定 | 动作 |
  | --- | --- | --- | --- |
  | `true` | `Alive` | `OursActive` | `FallThrough` |
  | `true` | `Unknown` | `OursActive` | `FallThrough`（保守，不打扰仍在播的会话） |
  | `true` | `Dead` | `Gone` | 立即回收；`Dead` **永远不会**判成 `BusyElsewhere` |
  | `false` | `Dead` | `Gone` | 立即回收 |
  | `false` | `Alive`/`Unknown` | 回退 `classify` | 同上面的三分类 |
  探测行和证据行都新增 `tcp_reachable`（bool，只有 `Alive` 为 `true`）与
  `tcp_liveness`（`alive`/`unknown`/`dead`）；媒体健康时不再因为 `/info` 报错
  就 fail-open 回收——那会把我们自己正在播的会话打断。
- **busy 队列永不放弃（Q34 §四）**：删掉了 `BUSY_LIMIT`、
  `ProbeAction::ReclaimBusyExhausted` 和 `busy_exhausted` warning。确认的
  `BusyElsewhere` 只做一件事：按下一次退避间隔排探测
  （`requeue_delay`：3 s → 6 s → 12 s → 24 s → 60 s，之后一直 60 s），
  不重建会话、不累计 takeover、不强制回收。任何非 busy 判决（`Free`/
  `OursActive`/fail-open）都会把队列重置。因此「另一台控制器合法地占着接收器
  一小时」的结果是：每分钟一次 `/info`，永远不 `preempted`。为了让队列真能跨过
  Q32 的 2 s `ended_at` 自愈，`Tracker::we_hold_session` 在**已确认的 busy 队列
  期间**固定返回 `false`（两次一致的探测是比过期 `ended_at` 更硬的证据），
  只有媒体恢复健康才能把它扳回 `OursActive`；队列本身仍由 `Free` 或媒体健康
  结束，不会 latch。
- **Q32 死锁与三处修复**：旧实现把「未预期 `TEARDOWN` 或 `403`」永久记为
  `session_ended`。`403` 的真实原因通常是认证/权限变化（HomePod 固件更新后
  要求密码、macOS 关闭访问控制等），会话其实仍属于我们；但 `session_ended`
  一旦置位就永不复位，`we_hold_session` 永远为 `false`，而 `statusFlags` 的
  bit17 又是我们自己会话置的位 —— 于是每次探测都判 `BusyElsewhere`，3 s 后
  再探，**永远没有出口**。修复分三处：
  1. **`we_hold_session` 不再 latch**：每次按 `rtp_healthy` 与 `ended_at` 实时
     推导。`rtp_healthy` 为真时**无条件**认为会话是我们的（WAC 的
     `control poison + healthy RTP ⇒ deferring teardown`）；`ended_at` 只在
     RTP 不健康时作证据，且只信 `ENDED_TRUST_WINDOW=2s`，超过窗口自动恢复为
     「持有」，所以任何控制面结论都不会把成员永久锁死。旧实现里「UDP 发送在
     接管后仍会成功、只看媒体健康会停在 `OursActive`」的担心，按 Q32 的裁决
     让位于 RTP 健康这一更硬的信号；真正的接管由控制面证据 + 有限期信任 +
     busy 上限共同兜底。
  2. **运行中 `/feedback` 拒绝只降级控制通道**（Q34）：`401`/`403`/`454`
     和任何其他非 200 状态都返回可重试的 `feedback_rejected`，由
     `Health::degrade()` 记为 `control_degraded` 并只发一条 warning；只要 RTP
     仍在发就继续播，不 `fail()`、不产生抢占证据、不写 `ended_at`、不再触发
     `/info` 探测。SETUP 阶段（媒体开始前）的 `401`/`403`/`454` 仍走终结性的
     `authentication_failed`，这条 Q16 边界没有变。真正表示「会话已结束」的
     控制面信号只剩未预期的 `TEARDOWN`；`Connection: close` 仍只是证据。
  3. **busy 队列不再有上限**（Q34 返工）：旧实现连续确认 `BusyElsewhere` 到
     `BUSY_LIMIT=3` 就强制回收，把「别人合法占着接收器」当成了「我们在抢」，
     还会把强制回收算进连续计数。现在确认的 busy 只排指数退避探测
     （3/6/12/24/60 s，封顶 60 s，永远继续），`BUSY_LIMIT` 与
     `busy_exhausted` 已删除；队列期间不回收、不计 takeover、不写 `ended_at`，
     任何非 busy 判决都会重置退避。Q34 §四的完整状态机见上。
  `ENDED_TRUST_WINDOW` 仍是**我们选的值**：Q32 只要求「可自愈」，没有给出 WAC
  的运行时参数；2 s 与引擎自己的 `MEDIA_HEALTH_WINDOW` 及 WAC 的 `2 s gap
  window` 对齐。busy 队列期间对 2 s 自愈的例外也只覆盖「已两次确认 busy」这一
  状态，媒体恢复健康或接收器变 `Free` 都会立即退出。
- **二次确认（Q32 第 3 问）**：`Free` 和 `BusyElsewhere` 都会改变状态，第一次
  probe 只排一次确认探测（`CONFIRM_DELAY=500ms`，**我们选的值**，Q32 未恢复
  WAC 的间隔），两次一致才执行；不一致时取非破坏性的结果 —— 任一为
  `OursActive` 就 `FallThrough`，否则继续排队，**绝不回收** —— 并打印
  `preemption_probe_disagreement`，行里带两次 outcome。`OursActive`、
  `Unknown`、探测错误/超时不做二次确认：前者不改变状态，后两者必须保持
  fail-open（立即回收），否则确认本身又成了新的卡死点。
- **`we_hold_session` 的来源**：不新增任何探测，用引擎已有的媒体证据
  `MediaTransport::media_healthy(now, MEDIA_HEALTH_WINDOW=2s)`（就是 WAC 日志
  里的 `rtp_healthy`：最近 2 s 内成功发过音频）作为第一维；第二维是接收器
  结束会话的时间戳 `ended_at`（只有未预期 `TEARDOWN` 会写它，`403` 不再写）。
  推导见 `preemption::we_hold_session(rtp_healthy, ended_at, now)`：
  - `rtp_healthy == true` ⇒ 必然持有，控制面信号不参与；
  - 否则 `ended_at` 在 2 s 内 ⇒ 不持有（排队/不误判成我们的）；
  - `ended_at` 为空或已超过 2 s ⇒ 持有（自愈）。
  `Connection: close` 不算结束会话（keep-alive/事件通道可以在会话仍属于我们
  时关闭）。媒体健康在 live 循环里总是可算（`Tracker` 只在 `start_feedback`
  之后、媒体循环里被轮询），没有「取不到」的分支；若真取不到，保守取
  `false`：`false` 只会把置位探测降级成 `BusyElsewhere`（排队，不回收、不结束
  会话），而 `true` 会在没有证据时说会话是我们的。
- **`OursActive` 不是抢占**：`action_for(OursActive) = FallThrough`，
  `poll_preemption` 返回 `PreemptionPoll::None`，不 `poison` 控制连接、不增加
  连续计数，照常走 `health.check()` / `check_control(media_healthy)` 这些原有的
  非抢占路径；会话结束时仍然正常回发 `TEARDOWN`。
- **判决表**（`action_for` / `action_for_result` 的纯函数部分；状态机会在其上
  再套二次确认和 busy 退避队列）：
  | 探测结果 | 首次动作 | 确认后 |
  | --- | --- | --- |
  | `Free` | 只排 500 ms 确认，不回收 | 两次一致才回收（算一次 takeover） |
  | `OursActive` | 不回收，`FallThrough` 到原非抢占路径 | 不做二次确认（不改变状态） |
  | `BusyElsewhere` | 只排 500 ms 确认 | 两次一致才按 3/6/12/24/60 s 退避再探；永不强制回收 |
  | `Gone`（存活探测判定 `Dead`） | 立即回收 | 不做二次确认；不算 busy、不计 takeover |
  | `Unknown` / `Err` / 超时 | 立即回收（fail-open） | 不做二次确认 |
- **连续计数只数「真接管周期」+ 时间窗 + yielding（Q34 §四）**：
  `TakeoverCounter` 按接收器（IP）计数，每个 live 播放进程跨 reclaim 复用。
  `TAKEOVER_LIMIT=3`、`TAKEOVER_WINDOW=30s`、`PROBE_TIMEOUT=2s`、
  `LIVENESS_TIMEOUT=2s` 和退避序列都是**我们选的值**，不是从 WAC 静态恢复的
  常数。一次「回收→接管」周期只有在**回收真的建立成功后**才开始：`run_stream`
  在新会话 SETUP/RECORD 完成、开始发包前调用 `reclaim_succeeded(now)`，随后的
  确认 `Free` 才调用 `takeover(now)`，只有落在这 30 s 窗口内才 +1，否则（包括
  从未成功回收过的第一次接管）重置为 **1**，不是 2。因此
  「接收器忙 ⇒ 确认 busy ⇒ 排队」这条路径永远不经过计数器；旧实现里「排队到
  上限强制回收、再让计数器背锅」的假周期不复存在。达到上限不再回收：返回不可
  重试的 `preempted` fault，`reason="limit"`，同时 `Tracker::suspend()`，之后
  `due()` 恒为 `false`、`scheduled()` 恒为 `None`——**yielding 之后不会再排任何
  探测**，桌面也不会自动重连。唯一复活方式是一次显式触发
  `Tracker::trigger(now)`（给用户点播放、或未来低频 `Free` 轮询预留的钩子；
  当前桌面在用户重新点播放时启动的是新进程/新 tracker，效果相同）。单次正常
  接管仍走 `reason="takeover"`；两种 reason 同时出现在 warning、fault 和桌面
  文案里，用户能区分「被接管」与「反复被接管后让步」。
- **回收在引擎内部完成**：live 循环捕获专用的 `preemption::Reclaim` 错误，
  **不把它当故障上报**；旧 `Member` 被丢弃，`run_live` 用新连接重新
  `run_stream`，`TakeoverCounters` 跨轮保留，并把「刚回收的 IP」带进下一轮
  （只有新会话建立成功才记为一次成功的 reclaim，供 30 s 周期窗口使用）。
  有限时长探测没有 live 循环，捕获 `Reclaim` 后转成终态
  `PREEMPTED(takeover)`。这样桌面不需要新增自动重连逻辑，正常会话行为也不变。
- **旧连接必须中毒**：回收前 `Connection::poison()`，之后任何 `write` /
  `finish_session` 都返回 `WireError::Poisoned`，不会把 TEARDOWN 或新请求
  发回已被接管的会话；回收只能走新连接（与 Q16 的「加密 RTSP 不能中途
  re-key」一致）。`run_live` 同时 `discard_stale()`，避免旧 capture backlog
  灌进新会话。
- **保留的边界**：我们自己发起的 `TEARDOWN` 仍不算抢占；**SETUP 阶段**
  （媒体开始前）的 `401`/`403`/`454` 仍是不可重试的
  `authentication_failed`（Q16），**运行中** `/feedback` 的任何拒绝状态都只走
  可重试的 `feedback_rejected` 降级路径（Q34）；`preempted` 的
  `Member::close()` 仍不回发 TEARDOWN。
- **不会形成无限循环/重连风暴**：一个 live 循环里每个探测周期最多回收一次；
  busy 时只按指数退避排下一次探测（3/6/12/24/60 s），不重建会话、不回收，
  队列无限长但不产生任何重连；`OursActive` 不排队，等下一个控制面信号再探；
  连续计数只在「成功回收后 30 s 内再次被接管」时才递增，到上限立即让步并
  停止一切探测。正常会话不探测，只有控制面出现证据后才发 `/info`；会改变状态
  的判决最多多一次 500 ms 的确认探测；存活探测是每次探测前的一条裸 TCP
  connect，不写任何字节。
- **可观测性**：`preemption_evidence` 行保留 WAC 的三项上下文
  （`policy`/`rtp_healthy`/`packets_prepared`）；`preemption_probe` 行现在也带
  这三项，并新增 `we_hold_session`、完整 64 位 `status_flags`（`0x%016x`）、
  `stereo_partner_unreachable` 遥测位（只上报，不参与判定），以及 Q34 的
  `tcp_reachable`（bool）和 `tcp_liveness`（`alive`/`unknown`/`dead`）——
  证据行和探测行都写，方便把「设备不在」与「设备在但被别人占着」分开。
  另外两个独立 warning code 让状态机不会静默循环：`preemption_probe_disagreement`
  记录两次探测不一致（带 `first`/`second` 两个 outcome），`preempted`
  （`reason="limit"`）记录让步；`busy_exhausted` 已随强制回收一起删除，
  不存在「排队撞上限」这个事件了。
- 测试（Rust 共 253 passed / 2 ignored：`--lib` 213 passed + 1 ignored、
  `main` 2、集成 38）：
  - `preemption.rs` 表驱动单测：`combine` 的整张表（`media_healthy` ×
    `Alive`/`Unknown`/`Dead`，含 `statusFlags` 缺省/0/活跃位变体）——
    `Dead` 永远是 `Gone`/回收且**不是** `BusyElsewhere`，健康媒体 + `Alive`/
    `Unknown` 是 `OursActive`，不健康才回退三分类；`ReceiverLiveness` 的
    `name()`/`reachable()`（只有 `Alive` 为 true）；真实
    `TcpStream::connect_timeout` 的 `Alive`/`Dead` 判定；
    `requeue_delay_is_the_exact_backoff_sequence`（3/6/12/24/60/60…）；
    `queueing_backs_off_forever_and_never_forces_a_reclaim`（一直 busy 也永远
    不 `Reclaim`）；`a_non_busy_verdict_resets_the_queue_backoff`；
    `a_confirmed_busy_queue_does_not_self_heal_into_ours_active`；
    `yielding_is_terminal_and_only_an_explicit_trigger_revives_it`
    （`suspend()` 后 `due()`/`scheduled()` 恒空，`trigger()` 才重新排探测）；
    takeover 周期：没有成功 reclaim 的接管永远是 1、成功 reclaim 后的接管才
    递增、超 30 s 重置为 1、到上限 yield 后重新从 1 开始、按 IP 独立；
    `a_dead_receiver_or_a_fail_open_probe_is_a_reclaim_but_not_a_takeover`。
    原有的三分类位定义、`u64` 高位、缺字段 `Unknown`、fail-open、二次确认、
    `a_403_cannot_wedge_the_probe` 等测试全部保留并通过。
  - `tests/preemption_probe.rs`：localhost `/info` 服务器，验证两次探测各开
    一条新连接；同一置位位图在 `we_hold_session` 两种取值下分别是
    `OursActive` / `BusyElsewhere`；bit14+bit7 与 `1<<40` 高位不影响 `Free`；
    缺 `statusFlags` 为 `Unknown`；连接拒绝/静默超时为 `Err` 且判决为回收；
    新增 Q34 集成测试：监听端口 ⇒ `Alive`、关闭端口 ⇒ `Dead`；
    `Dead` + 健康媒体 ⇒ `Gone`/回收且任何 `statusFlags` 下都不是 busy；
    `Unknown` + 健康媒体 ⇒ `OursActive`，不健康 ⇒ 回退 `Free`/`Unknown`。
  - `tests/continuity.rs`：运行中 `/feedback` 返回 401/403/454 时只把控制通道
    记为可重试的 `feedback_rejected`（不是 `authentication_failed` /
    `session_rejected`），`health.check()` 仍为 Ok、RTP 继续发包、只发一条
    `warning`、`take_preemption_signals()` 为空；控制降级 + 媒体不健康时
    `check_control(false)` 仍然失败。`session.rs` 的边界测试同时断言 SETUP 阶段
    的 401/403/454 仍是不可重试的 `authentication_failed`。
  - `tests/rtsp_contract.rs`：中毒连接不写出任何请求，`finish_session` 也
    不写 TEARDOWN。
  - `session.rs`：TEARDOWN + 两次 Free（媒体已停）时第一次返回 `Waiting`
    （排确认），确认后走 `PreemptionPoll::Reclaimed`，断言 `preempted`、旧连接
    中毒、warning `reason=takeover`、证据行带 `policy`/`rtp_healthy`/
    `packets_prepared`/`tcp_reachable`/`tcp_liveness`、probe 行带
    `we_hold_session`/`status_flags`/`stereo_partner_unreachable`、没有回发
    TEARDOWN；**`healthy_rtp_with_a_reachable_receiver_ignores_a_free_status_flags`**
    和 **`healthy_rtp_beats_a_receiver_teardown_signal`**：健康媒体 + `Alive`
    永远是 `None`（`ours_active`），不中毒、不计数、关闭时仍正常发 TEARDOWN；
    **`ended_session_with_unhealthy_rtp_is_busy_elsewhere`**：两次确认后
    `Waiting`（busy），调度时间 = 确认时刻 + `requeue_delay(0)`，不回收、
    不出现 `busy_exhausted`/`preempted`；**`a_queueing_busy_receiver_never_reaches_the_yield`**：
    24 轮确认 busy（跨过 2 s 自愈窗）仍不 yield、不中毒、计数器为 0；
    **`a_dead_control_port_reclaims_instead_of_a_generic_fault`**：TCP 被拒 ⇒
    `gone`/回收、`tcp_reachable=false`、不计 takeover、无 `preempted`；
    缺字段（媒体不健康）⇒ fail-open 回收；`hitting_the_consecutive_limit_yields_with_reason_limit`
    用新计数 API 播种两个「成功回收后接管」周期，第 3 次触顶后断言
    `reason="limit"`、`suspended()`、`scheduled()=None`，且 1 s/60 s/1 h/24 h
    后都 `due()==false`——证明 yielding 之后不会再排任何探测。
  - 桌面 `IpcFailureTests`：`reason=limit` 使用「反复接管后让步」文案，且
    仍是 `PlaybackState.Preempted`、不自动重连（Q34 未改桌面状态机）。
- **没做/没验证**：
  - 真机 `statusFlags` 仍未取样（Q32/Q34）：HomePod 在我们自己播放、空闲、
    被接管三种状态下 bit17 是否分别置位/清零、接管瞬间 `/info` 是否仍可读、
    字段是否始终是整数，都没有实测。代码已按 WAC 的位定义实现，异常一律按
    `Unknown` fail-open 回收。
  - 真机 403 语义：现在运行中 `/feedback` 403 只降级控制通道并保持会话
    （Q34，WAC 的 `keeping healthy media session`）。真机上 HomePod 在什么条件
    下会对 `/feedback` 回 403、音频是否仍然正常，仍未实测。
  - RTP 健康与真接管：按 Q32 的无条件规则，UDP 发送仍「成功」时 bit17 会被判成
    `OursActive` 而不回收。真接管时 ICMP/端口关闭能否在多长时间内让
    `media_healthy` 变假、从而进入 busy 路径，未实测，见 Q34。
  - TCP 存活探测的真机错误码（Q40，待答复）：Q34 只规定「被拒/超时 ⇒ `Dead`，
    其他错误 ⇒ `Unknown`」。Windows 对同一子网内已关机/拔电的 HomePod 经常返回
    `HostUnreachable`/`NetworkUnreachable`（10065/10051）而不是 refused/timeout；
    按当前实现会落到 `Unknown`（保守 ⇒ `OursActive`），于是「设备关机」不会
    回收，只能等媒体窗口自己过期。真机返回哪种错误、是否应把 host/network
    unreachable 也算 `Dead`，未实测。
  - `ENDED_TRUST_WINDOW=2s`、`CONFIRM_DELAY=500ms`、退避 3/6/12/24/60 s、
    `TAKEOVER_LIMIT=3`、`TAKEOVER_WINDOW=30s`、`LIVENESS_TIMEOUT=2s` 都是
    **我们选的值**：2 s 与 WAC 的 gap window / 引擎 `MEDIA_HEALTH_WINDOW` 对齐，
    其余没有 WAC 依据（Q34 只规定「永不放弃」和「只数真周期」）。
  - busy 队列无限长的代价：另一台控制器合法地占着接收器时，我们会一直每 60 s
    探一次 `/info`（外加一条裸 TCP connect），直到它空出来；这是 Q34 要求的
    行为，但真机上 60 s 一次的探测开销和「什么时候该放弃」未实测。
  - 存活探测只证明「控制端口还在监听」：HomePod 在开机但 RTSP 服务异常、
    或中间防火墙只挡 UDP 时，`Alive` 仍可能把已失效的会话判成 `OursActive`。
    这种情况仍靠 `MEDIA_HEALTH_WINDOW` 兜底，未在真机验证。
  - 没有抓包验证 HomePod 被接管时 `/info` 响应是否仍可读、是否要求认证、
    `statusFlags` 是否在接管瞬间变化。
  - 立体声对中一个成员在探测时最多阻塞 2 s；本地测试未覆盖两台接收器同时
    被接管的时序。

## 还没做

| 项目 | 现状 | 备注 |
| --- | --- | --- |
| P1-10 标定流程 | 未开始 | 需要真机测延迟/丢包，按报告是「用户自助标定」 |
| P2-12 热加入 | 未做 | 目前只做组创建与成员重试，运行中增删成员未实现 |
| P3-4 MRP 隧道 | 未开始 | 大工程 |
| P3-5 经典 RAOP / 初代 AirPlay | 未开始 | 大工程 |
| P3-6 `bufferStream` | 未开始 | 报告建议暂缓：弱网才有收益，改造成本大 |
| P3-9 AAC-ELD / OPUS | 未开始 | 需要接收端能力探测与编码器验证 |
| P0-4 封面（`image/jpeg`） | 未做 | 尺寸/格式约束需要设备确认，见 Q10；失败也不能干扰音频 |
| P0-3 帧级 `interval_correction` | 未做 | 见上文，现用连续重采样代替 |
| §12.3 会话摘要 | 未做 | 每次会话结束输出一行 JSON/CSV 的汇总尚未实现 |
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

## P2-13b / P2-13c 组内时钟域（已完成，Q33 修正）

`native/airflash-engine/src/profile.rs`、`src/session.rs`、
`src/receiver_capabilities.rs`、`src/session_simulation.rs`、
`tests/compat_profile.rs`

- **缺陷（P2-13b）**：`Member::connect()` 在每个成员自己的 `/info` 之后各调一次
  `CompatProfile::derive()`（内部是单成员协商），于是立体声对的两个成员可能
  一个拿到 PTP、一个拿到 NTP。实际触发是「一个成员有 bit40、另一个没有」：
  有 bit40 的按 AP2 走 PTP，没有的退回 NTP，同一组出现两个时钟域。WAC 的
  `CreateTopologyPlan` 不是这样做的，它先汇总整组再给统一结论。
- **整组规则（Q33 修正后，`profile::select_timing`）**：

  | 场景 | 结果 |
  | --- | --- |
  | 空组 | NTP（不会发生，防御性默认） |
  | 单成员 | 完全沿用 97 行表刻画的单机行为（`solo_timing()`），PTP/NTP 不变 |
  | 任一成员没有 AirPlay 2 证据（bit40 且 `srcvers >= 354.54.6`） | **整组 NTP** |
  | 多成员且全部有 AP2 证据（HomePod 组 / Mixed Apple / 未知厂商 AP2 组） | **整组 PTP，无一例外** |

  「Apple TV 单机 = NTP」只在 `n == 1` 分支成立；一旦进多设备，成员的单机
  答案不再参与整组决策，Apple TV 必须作为 PTP follower 加入发送端的时钟域。
  这是 P2-13c 相对 P2-13b「组内取最保守」的修正：旧规则会把 Mixed Apple
  group 错误地压到 NTP，和 WAC 的 `n > 1` 分支相反。
- **只算一次、所有成员共用**：`TimingEvidence`、`TimingPlan` 与
  `CompatFingerprint::negotiate_planned(plan, …)` 不变。`session.rs` 的
  `run_stream` 仍是两阶段：先对全部成员做 `/info` 探测（失败仍按原逻辑重试
  一次），用整组指纹算 `select_timing`，再给每个成员用同一个 `TimingPlan`
  做 SETUP。P2-13c 移除了「任一成员显式 NTP 就把整组折到 NTP」的预处理：
  组计划 PTP 时，显式 NTP/PTP 都由组决定覆盖，成员照常收到
  `timingProtocol=PTP`、`timingPeerInfo`、`SETPEERS`，加入发送端的 PTP 域；
  组计划 NTP 时显式 PTP 被降级、`Transport::Auto` 只能选 Realtime
  （buffered 是 PTP-only），显式 Buffered 或 `follow_receiver_clock` 在组
  NTP 下直接报错。单成员走 `TimingPlan::single`，`Timing::Inherit` 仍用
  `inherited_ptp`，行为与改造前一致。
- **Apple TV 进组即 PTP follower（P2-13c）**：`negotiate_planned` 在
  `plan.is_group()` 时先认组计划，再考虑成员自己的 `timing` 请求；组 PTP
  下 Apple TV 的型号（`ptp_expected() == Some(false)`）不再能否决 PTP。
  对应 WAC `GroupPtpDiagnosis` 的硬要求：组计划是 PTP 时，诊断对**每一个
  成员**都要求 `PtpOffset` 直方图且 ≥30 个样本；一台还在走 NTP 的 Apple TV
  不可能产出这个量。AF 侧的证据是 SETUP 里该成员带 `timingPeerInfo`（而不是
  `timingPort`）并执行 `SETPEERS`/`ptp.activate`，`session_simulation` 的
  mock 现在逐成员记录这一点。
- **`timingProtocol` 是设计选择，不是协议限制**：`timingProtocol` 在协议层是
  每次 SETUP 的 plist 字段，所以「每成员不同 timing」在协议上合法；AF 禁止
  它是**设计决定**，对齐 WAC 的运行时断言
  （`snapshot.Timing == plan.Timing`，不一致即判整份遥测无效）。这段说明已
  写进 `negotiate_planned` 的文档注释，避免以后有人误以为协议强制一组一域。
- **分歧告警降级（P2-13c）**：`group_timing_warning` 改名为
  `group_timing_diagnostic`，不再发 `{"event":"warning", ...}`，只把
  `group_timing_divergence: <每个成员的 receiver_id/IP、是否 AP2、单机会选
  什么、显式请求什么>; forced PTP/NTP for the whole group` 写到引擎 stderr
  日志。桌面端 `SessionController` 的 `warning` 通道因此完全看不到它，
  「SETUP 前 `startedAt == 0` 被丢弃」的问题也随之消失。内容仍保留在日志与
  诊断导出里；一条组建立最多一行，不按成员或按包重复。
- **预判（`receiver_capabilities`）不变**：`choices()` 仍用
  `TimingPlan::single(fingerprint, codec)`，输入仍只有 `supportedFormats` +
  对端 `features`（没有 model/srcvers），IPC 的 `selected_timing` 输出没有
  变化。它是**单接收器**答案：一台 Apple TV 的预判仍显示 `ntp`，而实际加入
  多设备组时运行时会按组计划给它 PTP；组时钟不在这条 per-receiver IPC 响应
  里，统一在 `session::run_stream` 决定。代码注释已写明这个边界。
- **测试**：`session_simulation.rs` 的 mock 新增 model 覆盖与
  `timingPeerInfo` 记录；新增「Mixed HomePod + Apple TV 全 AP2 → 两人都
  PTP，且 Apple TV 的 SETUP 带 timingPeerInfo」「一人缺 bit40 → 两人都 NTP，
  且不发用户可见 warning」两个握手级测试；原全 AP2 HomePod 组测试保持。
  `tests/compat_profile.rs` 的 97 行表一行没改，另追加组规则（HomePod 组 /
  通用 AP2 组 / Mixed Apple 组 / 缺 bit40 降级 / 单机不变）与
  `negotiate_planned` 的断言。Rust 全量：lib 203（202 passed + 1 ignored），
  main 2，集成测试 34 passed（soak 1 ignored），共 238 passed / 2 ignored。
- **没验证/没做的**：
  - 没有真机，也没有抓包确认：一台真实 Apple TV 加入 PTP 组后是否真的作为
    PTP follower 跟发送端时钟（`timingPeerInfo` 被接受、PTP offset 样本
    ≥30）；这是 Q33 之后最关键的未知项。
  - 没有真机确认「AP1 + AP2 组」在真实接收器上接受整组 NTP；老 HomePod 对
    PTP 的偏好只在模拟器里成立。WAC 的证据支持整组走 Classic RAOP/NTP，
    但真机是否出声仍未验证。
  - `/info` 的 `name` 仅作为 model 缺失/为 `Unknown` 时的兜底；没有真实
    `/info` 样本证明各型号的 `name` 字符串，也没有真机验证。
  - Q33 的 `n == 1 && allHomePod && isPcm -> NTP` 分支没有采用：AF 的 97 行
    表把 HomePod 单机（含 PCM）刻画为 PTP，且本次约束要求单机行为不变。
    两者不一致，记入 Q38 待答复。
  - 组 NTP 下 `follow_receiver_clock` 直接失败（无法既跟随接收器 PTP 又整组
    NTP），UI 目前只按用户设置下发，未做提前禁用/提示。

## P3-2 桌面半：TXT 能力标注与门禁错误分类（已完成）

`desktop/AirFlash.Core/AirPlayCapability.cs`、`GateFailure.cs`、`Receiver.cs`、
`SessionController.cs`、三语 `Strings.*.json`、`desktop/AirFlash.Tests`

- **两段式证据**：按 Q18/Q29 的答复，AirPlay 2 只由 TXT 决定，PTP/NTP 仍由
  引擎的型号 + 拓扑决定；桌面这一层不碰引擎，也没有改 IPC。
- **路由与标注分开（Q36）**：这是两个不同的问题，代码里也拆成两个判定：
  - **路由**（`AirPlayRouting.SupportsAirPlay2`）严格照 WAC 的布尔：
    `(features & (1 << 40)) != 0 && srcvers >= 354.54.6`。`NotAirPlay2` 和
    `ClaimsAirPlay2Outdated` 都返回 `false`，都走 classic 路径，没有因为
    「设备自称 AP2」而放宽。
  - **标注**（`AirPlayCapabilityClassifier.Classify`）只回答「我们知道了
    什么」，用四态。路由布尔会把「不支持」和「自称支持但固件过旧」压成同一个
    `false`，而这两者的处置完全不同（换设备 vs 升级固件），所以不能共用一套
    判定。
- **TXT 解析**：`Receiver` 新增 `SourceVersion`（TXT `srcvers` 原文），`Features`
  保持原有 `features`/`ft` 回退与两段 32 位字拼接。双段形式 `"0x...,0x..."`
  低字在前、高字在后，bit40（高字 `0x100`）现在有专门的测试钉住；只读第一段
  会把真 AP2 设备误判成 `NotAirPlay2`，这是 Q36 §五 指出的隐藏风险。
- **版本比较**（`AirPlayVersion.TryParse`）：点分十进制，至少两段；
  2–4 段按 `System.Version` 比较，第 5 段起忽略；每段允许前后空格；空串、单段、
  非数字、负号一律不满足。`354.54.6`/`354.54.7`/`366.0` 通过，
  `354.54.5`/`354.54`/`220.68`/`354` 不通过。路由与标注共用这份解析，但用不同
  的判定逻辑。
- **列表标注**：单台物理接收器的 `Detail` 从「型号」改成「型号 · 能力」，
  四态一一对应：
  | TXT 证据 | 状态 | 列表文案 |
  | --- | --- | --- |
  | `features` 缺失 | `Unknown` | `AirPlay 2 support unknown` |
  | bit40 = 0 | `NotAirPlay2` | `Classic AirPlay only` |
  | bit40 = 1，`srcvers` 不可解析 | `Unknown` | `AirPlay 2 support unknown` |
  | bit40 = 1，`srcvers` < 354.54.6 | `ClaimsAirPlay2Outdated` | `AirPlay 2 claimed, firmware too old` |
  | bit40 = 1，`srcvers` >= 354.54.6 | `AirPlay2` | `AirPlay 2` |
  没有 feature 证据时绝不显示 classic；手工添加的接收器同样显示 unknown。
  「自称 AP2 但固件过旧」有独立文案，不再和「仅经典 AirPlay」共用一条。
  原生立体声组继续只显示 `HomePod stereo · n/2`，软件组继续只显示
  `Software stereo · experimental`；组不叠加能力标注，只有单台物理接收器带
  新标注。
- **门禁分类**：`GateFailure.Classify` 复用现有 `EngineNotice` /
  `SessionController.FailureMessage`，没有新增 IPC 字段或第二条错误通道：
  | 证据 | 分类 | 用户文案 |
  | --- | --- | --- |
  | 引擎 `authentication_failed`，或 `session_rejected` + TXT `pw=true`；或 MFi 文本（仅展示启发式） | 密码/配对（通用认证失败） | 需要密码或配对；MFi 文本换成 MFi 文案 |
  | setup 通道 + TXT 证明 classic-only（组内任一成员 `NotAirPlay2`） | 仅经典 | AirFlash 暂不能推流 |
  | 桌面 `network_error`（DNS/`SocketException`）、`socket_error`、`connection_reset`、`read_timeout`、`write_failed`、`peer_closed`、`feedback_timeout` | 网络 | 检查设备与网络 |
  | 其他（含 `preempted`/`timeline_expired`/`clock_lost`） | 不分类 | 保留引擎的 `Detail` |
  桌面内部新增的 `network_error` 只用于 `EngineNotice`，不进入 IPC 契约。
  `read_timeout` 与 classic 同时出现时优先 classic（经典设备常常接受 TCP 但不回
  AirPlay 2 `/info`）；真正的连接失败/DNS 失败优先网络。setup 以外的 classic
  证据不改文案，避免运行中网络故障被误标成能力门禁。
- **MFi 降级为展示启发式（Q37）**：WAC 定义了 MFi 错误变体但从不检测它
  （"not implementable without Apple hardware"），TXT 里也没有已确认的 MFi 位。
  因此桌面不再保留独立的 MFi 门禁分类：消息含 `MFi`（或引擎 code
  `mfi_required`，目前不会发出）只用于把文案换成友好的「需要 MFi 认证」，
  权威分类仍是通用认证失败（`PasswordOrPairing`）。也没有给引擎加
  `mfi_required`，因为它没有可靠触发条件，会变成死代码（Q37）。
- **测试**：四态逐行（两个 `Unknown` 来源都覆盖）、路由布尔对
  `Unknown`/`ClaimsAirPlay2Outdated` 保持 `false`、`features`/`ft` 两段 32 位
  解析（真形状 `"0x4A7FCA00,0x100"` 的 bit40 在高字 → `AirPlay2`；高字无 bit40
  与单段字符串各一条）、TXT `srcvers`、单机四态与组/软件组 `Detail`、门禁分类
  表（classic、密码、网络、未知、组内 classic、非 setup 不误标）、MFi 文本
  只换文案不改权威分类，以及 3 条 `SessionController` 消息落地测试。两段解析
  现在由测试固定，不再只靠代码阅读确认。
- **验证**：本节提交在基于 `cfa62a9` 的树上跑过——Rust lib 198 passed
  + 1 ignored、main 2、集成 34 passed + 1 ignored（soak），共 234 passed /
  2 ignored；clippy `-D warnings` 无告警；`build-native.ps1 -Check` 通过；
  .NET 547 通过；pytest 141 通过；ruff 无问题。
- **P3-2 整体状态**：引擎半（`profile.rs::is_airplay2`、`ptp_expected`，见上文
  「P3-2 连接前预判」）此前已完成，本轮补完桌面半，因此 **P3-2 的引擎半和
  桌面半都已落地**。`还没做` 表里本来就没有 P3-2，不需要删除。
- **没验证 / 待确认**：没有真机，无法确认 AirPort Express 的真实 TXT 是否就是
  `srcvers=220.68`、HomePod 是否就是 `srcvers=366.0`，也无法确认 classic 设备
  拒绝 AirPlay 2 SETUP 时实际落在 `setup/protocol_error` 还是
  `setup/read_timeout`；分类优先级是按协议语义和 WAC 的两段模型推出来的。
  `acl`（报告里的密码标志）也没有并入 `RequiresPassword`，因为缺少真实 TXT
  样本，暂不改现有 `pw` 语义。门禁只把 `NotAirPlay2` 当作 classic 证据，
  `ClaimsAirPlay2Outdated` 设备 SETUP 失败时仍落到引擎 detail/网络/密码分类；
  是否需要独立门禁文案、组规则是否要把它算作 classic，见 Q39。
