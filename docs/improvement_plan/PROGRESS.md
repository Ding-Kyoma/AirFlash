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

## P3-3 抢占识别（WAC 模型 + `statusFlags` 位定义 + Q32 死锁修复 + Q34 busy/存活 + Q42 存活升级 + Q46 网关参照物，已完成）

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
- **Q34 的第四维：TCP 存活探测（Q42 修正）**：`statusFlags` 只说明「接收器上有没有
  会话」，不说明「那台设备还在不在」。`receiver_liveness(ip, port, 2s)` 对**控制端口**
  做一次裸 `TcpStream::connect_timeout`（接通即 `Alive`，拿到 socket 立刻丢弃，不发
  任何 RTSP 请求），结果按 Q42 分成**四态** `ReceiverLiveness { Alive, Refused,
  Dead, Unknown }`：
  | 探测结果 | 物理含义 | 状态 |
  | --- | --- | --- |
  | 接通 | 主机在、端口在服务 | `Alive` |
  | RST 回来（`ConnectionRefused`） | **主机活着、协议栈在跑，只是那个端口没监听** | `Refused` |
  | 超时 / `HostUnreachable` / `NetworkUnreachable` | 主机不响应 / 明确不可达 | `Dead` |
  | 其他错误（防火墙、瞬时本机 socket 错误、IPv6 邻居发现失败…） | 既不能证明活也不能证明死 | `Unknown` |
  Q42 更正了 Q34「被拒 ⇒ `Dead`」的说法：**收到 RST 是对端网络栈回应了我们**，那是
  「活着但没在服务 AirPlay」，不是「关机」。动作上 `Refused` 与 `Dead` 一样都读不到
  `statusFlags` ⇒ 同样 fail-open 回收，但**状态不合并**：日志里 `tcp_liveness` 分别是
  `refused` 和 `dead`，排障时能区分「设备被我判死」和「设备拒连」。`combine` 的整张表：
  | `media_healthy` | 存活状态 | 判定 | 动作 |
  | --- | --- | --- | --- |
  | `true` | `Alive` | `OursActive` | `FallThrough` |
  | `true` | `Unknown` | `OursActive` | `FallThrough`（保守，不打扰仍在播的会话） |
  | `true` | `Dead` / `Refused` | `Gone` | 立即回收；**永远不会**判成 `BusyElsewhere` |
  | `false` | `Dead` / `Refused` | `Gone` | 立即回收 |
  | `false` | `Alive` / `Unknown` | 回退 `classify` | 同上面的三分类 |
  媒体健康时不再因为 `/info` 报错就 fail-open 回收——那会把我们自己正在播的会话打断。
- **Q42/Q46 的失联升级计时器：`Unknown` 有耐心上限，不枚举 errno**：`Unknown` 只是「暂时
  不知道」，不是「永远算我的」。`LivenessTracker { consecutive_failures, last_ok }`
  在每次探测后给出 `Verdict { Alive, Dead, Unknown }`：
  `Alive` ⇒ 计数清零并刷新 `last_ok`；`Refused`/`Dead` ⇒ 直接 `Dead`；`Unknown` ⇒
  计数 +1，达到 **`LIVENESS_PATIENCE = 5`**（连续 5 次什么也没证明的探测）就升级成
  `Dead`，另有一条**墙钟兜底**：从第 2 次连续失败起，若距 `last_ok` 已超过
  `LIVENESS_PATIENCE_WINDOW = 60 s` 也升级。**墙钟条款按 Q46 保持收窄版**
  （`consecutive >= 2` 且 `now - last_ok >= 60 s`）：Q46 明确说这版比答复里的示意代码
  更对——**长时间安静之后的一次孤立异常**正是这条规则最不该惩罚的瞬时本机抖动
  （防火墙刷新、`WSAENOBUFS`、路由表抖一下），一次错误就判死会打断本来正常的会话；
  收窄后它仍然覆盖原先要解决的**稀疏失败**（每 40 s 失败一次，连续计数永远到不了 5，
  但第 2 次时距 `last_ok` 已 80 s ⇒ 升级）。`last_ok` 只在**探测成功**时刷新，不被
  「媒体健康/UDP 发送成功」刷新，否则设备走了时钟也永远不过期。
  为了让这条计时器真能生效，判决未定时 `Tracker::await_liveness` 会在
  `LIVENESS_RETRY = 3 s` 后再采一次样（否则反馈线程已经退出、不会再产生信号，一次
  `Unknown` 就永远是唯一一次采样，耐心上限形同虚设）；采样循环有界——第 5 次必然
  `Dead` 并回收。busy 队列和 yielding 终态不被这个计时器改写。
  **时间尺度按 Q46 调整（6 s 对 Wi-Fi 漫游太短，建议 15 s）**：第一到第五次失败之间是
  `(LIVENESS_PATIENCE - 1) × LIVENESS_RETRY = 4 × 3 s = 12 s` 的间隔，加上判决那一轮
  探测本身的耗时（≤ `LIVENESS_TIMEOUT` = 2 s），即**约 12–14 s**，落在用户要的 15 s
  附近；真机断电时 connect 一般在 ARP 失败后约 1 s 返回 `HostUnreachable`，实际就在
  15 s 量级。取值理由：跨 AP 重关联 + DHCP 是 5–15 s，`Unknown` 链必须覆盖它；判死早了
  是**可听的中断**（要重建会话），判死晚了只是多等几秒——**不对称，所以宁可保守**。
  Q46 建议的「重试间隔做退避 3→4→6→9→13 s」没有采纳：五次采样会被摊到约 35 s，直接
  违背同一答复给出的 15 s 预算，而探测开销本来就被采样次数（5 次）限住，与固定间隔
  无关。Q46 另建议把两个值做成配置项，也没有做：本轮任务要求不新增用户配置，数值先按
  真机反馈再调。效果：设备真关机/不可达时约 15 s 内回收，不再依赖 `MEDIA_HEALTH_WINDOW`
  兜底（UDP 发送在关机后仍会「成功」，那个窗口实际上永远不会过期）；`Dead`/`Refused`
  这类**确定性**失败仍然立即回收，不进入这条计时器。
- **Q42/Q46 的多接收器交叉校验：全失败时先探网关（对照实验）**：单看一台设备分不清
  「它关机了」和「本机路由表/网卡/Wi-Fi 坏了」，但同一轮里所有接收器都失败时，这两种
  解释需要一次额外探测来分开。Q46 的裁决是**加参照物，而不是加轮数**：同一轮额外探一次
  **默认网关**——它与本机接收器走同一条链路，但不共享接收器的供电状态。旧的「全失败
  = 本机问题、永远生效」因此被替换，不再是启发式。
  `all_receivers_failed(probes, receivers)` 仍然只在 **接收器数 > 1、本轮每个成员都被
  探测过、且所有探测都没证明主机回应** 时为真（`Alive`/`Refused` 都算「主机回应过」，
  所以**全拒连不是本机网络故障**——设备活着，只是不在服务；少探一台也不算，那台设备的
  判决照常生效）。为真时 `judge_all_failed` 给出三选一：
  | 网关参照 | 结论 | 动作 |
  | --- | --- | --- |
  | 有回应（接通或 RST） | `DevicesGone` | 设备真的都关了 ⇒ 每台设备的 `Dead` 判决照常生效、照常回收 |
  | 无回应（所有探测端口静默） | `LocalNetwork` | 是本机网络 ⇒ 不标死任何设备、不回收 |
  | 拿不到默认路由 | `LocalNetworkUnproven` | 先抑制，但**最多 `GATEWAY_FALLBACK_WINDOW = 60 s`**；超时后 `FallbackExpired`，停止抑制（Q46 的兜底：时间只是最后手段） |
  命中抑制时这一轮**不把任何设备标成死**：每个成员的 `Dead` 判决被降级成
  `Verdict::Unknown`（健康媒体 ⇒ `OursActive` ⇒ 不回收），升级计数既不增加也不清零
  （这不是关于这台设备的证据），也不排「升级重采」——但**会**照常按 `LIVENESS_RETRY`
  重采一轮：恢复的网关必须能结束抑制，没有网关时 60 s 兜底也只能靠这些轮次推进。
  日志只在**抑制状态变化时**发一条 `preemption_local_network`（带 `receivers`、每台设备
  的原始 `probes`、`verdict`、`gateway`（`reachable`/`unreachable`/`null`）和
  `all_failed_for_ms`），不会每 3 s 刷屏；探测行/证据行的 `local_network_suspected` 仍
  标记被抑制的轮次。
- **网关地址怎么来（没有新配置项）**：`default_gateway()` 直接调 Windows
  `iphlpapi.dll` 的 `GetIpForwardTable`（IPv4 路由表），在 `0.0.0.0/0` 的行里取 next hop
  非 0 且 `Metric1` 最小的一条。这与 Q46 提的 `GetAdaptersAddresses` 的
  `FirstGatewayAddress` / `GetIpForwardTable` 是同一份数据；引擎没有启用 `windows` crate
  的 `Win32_NetworkManagement_IpHelper` feature（本任务不扩依赖），所以用
  `#[link(name = "iphlpapi")]` 直接声明这两个导出。表由调用方分配（先用空缓冲区取大小），
  因此用 `Vec` 释放，不调 `FreeIpForwardTable`。**取不到就返回 `None`，绝不编一个
  `192.168.1.1`**：伪造的默认网关会把「判据」又变回「猜」，所以这时走上面的 60 s 兜底，
  事件里 `gateway=null`。实现于 `preemption.rs` 的 `#[cfg(windows)]` 分支；非 Windows
  构建返回 `None`（引擎只在 Windows 运行）。
- **参照物探测只看「有没有回应」，不看「有没有服务」**：`reference_reachability` 依次
  TCP 连 `443`、`80`（每个 `GATEWAY_PROBE_TIMEOUT = 1 s`），**接通和
  `ConnectionRefused` 都算回应**（RST 同样证明对端协议栈在链路上），只有所有端口都静默
  才是 `Silent`。网关探测结果**不会**变成 `ReceiverLiveness`，也从不进入任何 `Tracker`：
  参照物失败是关于本机网络的证据，不是关于某台接收器的证据（测试
  `a_failed_gateway_probe_is_never_a_dead_receiver`）。代价：如果路由器对这两个端口
  静默丢包（而不是回 RST），会被读成「本机网络」，此时「全组关机」不会被回收——这是
  Q46 明确选择的方向（假阴性优于假阳性），未在真机路由器上实测，见下方「没做/没验证」。
- **UDP「发送成功」永远不是存活信号（Q42 §五）**：UDP 无连接，内核只把包交给网卡，没有
  任何投递确认；ICMP port-unreachable 在 Windows 上默认异步且经常被丢弃。所以引擎**不**
  把「UDP 发送成功」当作设备还活着的证据，存活判据只有 TCP 探测 + `MEDIA_HEALTH_WINDOW`
  兜底；Q42 之后兜底不再是主力，主力是上面的升级计时器。
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
  | `Gone`（存活探测判定 `Dead`/`Refused`，或升级后的 `Unknown`） | 立即回收 | 不做二次确认；不算 busy、不计 takeover |
  | `Unknown` / `Err` / 超时 | 立即回收（fail-open） | 不做二次确认 |
- **连续计数只数「真接管周期」+ 时间窗 + yielding（Q34 §四）**：
  `TakeoverCounter` 按接收器（IP）计数，每个 live 播放进程跨 reclaim 复用。
  `TAKEOVER_LIMIT=3`、`TAKEOVER_WINDOW=30s`、`PROBE_TIMEOUT=2s`、
  `LIVENESS_TIMEOUT=2s`、`LIVENESS_PATIENCE=5`、`LIVENESS_PATIENCE_WINDOW=60s`、
  `LIVENESS_RETRY=3s`、`GATEWAY_PROBE_TIMEOUT=1s`、`GATEWAY_PROBE_PORTS=[443,80]`、
  `GATEWAY_FALLBACK_WINDOW=60s` 和退避序列都是**我们选的值**，不是从 WAC 静态恢复的
  常数（Q46 只给了「网关参照物 + 约 15 s」这个方向）。一次「回收→接管」周期只有在**回收真的建立成功后**才开始：`run_stream`
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
  `stereo_partner_unreachable` 遥测位（只上报，不参与判定），以及 Q34/Q42 的
  存活字段：`tcp_reachable`（bool，只有 `Alive` 为 `true`）、`tcp_liveness`
  （**四态** `alive`/`refused`/`dead`/`unknown`，原始探测结果）、
  `liveness_verdict`（升级/交叉校验后的判决 `alive`/`dead`/`unknown`）、
  `liveness_failures`（连续未定次数）和 `local_network_suspected`（本轮被 Q46 的
  全失败规则抑制）——证据行和探测行都写，方便把「设备不在」「设备拒连」「设备在但被
  别人占着」「问题在本机」四种情况分开。另外三个独立 warning code 让状态机不会静默
  循环：`preemption_probe_disagreement` 记录两次探测不一致（带 `first`/`second` 两个
  outcome）、`preemption_local_network` 记录「多接收器本轮全失败且网关参照物没有排除
  本机网络」（带 `receivers`、每台设备的原始 `probes`、`verdict`、`gateway`、
  `gateway_probed` 和 `all_failed_for_ms`；只在抑制状态变化时发一条）、
  `preempted`（`reason="limit"`）记录让步；`busy_exhausted` 已随强制回收一起删除，
  不存在「排队撞上限」这个事件了。
- 测试（本分支合并 `main` 后 Rust 共 297 passed / 2 ignored：`--lib` 251 passed + 1
  ignored、`main` 2、集成 44；P3-3 本轮新增/改写的用例见下）：
  - `preemption.rs` 表驱动单测：`combine` 的整张表（`media_healthy` ×
    `Alive`/`Refused`/`Unknown`/`Dead`，含 `statusFlags` 缺省/0/活跃位变体）——
    `Dead`/`Refused` 永远是 `Gone`/回收且**不是** `BusyElsewhere`，健康媒体 +
    `Alive`/`Unknown` 是 `OursActive`，不健康才回退三分类；`ReceiverLiveness` 的
    `name()`（四态）/`reachable()`（只有 `Alive` 为 true）/`host_answered()`
    （`Alive`/`Refused` 为 true）；`liveness_from_error` 的 Q42 映射
    （`ConnectionRefused` ⇒ `Refused`；`TimedOut`/`HostUnreachable`/
    `NetworkUnreachable` ⇒ `Dead`；`ConnectionReset`/`NotConnected`/
    `PermissionDenied`/`Other` ⇒ `Unknown`）；真实
    `TcpStream::connect_timeout` 的 `Alive`/关闭端口判定；升级计时器：
    `an_unknown_chain_escalates_to_dead_and_one_ok_resets_it`（`LIVENESS_PATIENCE`
    次连续 `Unknown` 才 `Dead`、一次 `Alive` 清零并刷新 `last_ok`）、
    `refused_and_dead_are_decided_without_waiting`、
    `the_patience_window_only_escalates_after_a_second_failure`（60 s 墙钟条款
    只从第 2 次连续失败起生效）、
    `a_local_suspect_round_neither_counts_nor_resets_the_device_failures`、
    `an_undecided_round_schedules_another_sample`（`await_liveness` 排
    `LIVENESS_RETRY`，重复的相同样本不重复打印）、
    `the_liveness_timer_never_cuts_a_busy_queue_short`（busy 退避/yielding 不被
    改写）、**`the_all_failed_rule_needs_the_gateway_reference`**（全失败为真、
    有一台 `Alive` 为假、全 `Refused` 为假、单台为假、少探一台为假；网关
    `Answered` ⇒ `DevicesGone` 不抑制、`Silent` ⇒ `LocalNetwork` 抑制、拿不到网关
    ⇒ 窗口内 `LocalNetworkUnproven` 抑制、窗口到点 `FallbackExpired` 停止抑制）、
    **`a_gateway_probe_is_judged_by_answering_not_by_serving`**（监听端口 ⇒
    `Answered`、关闭端口 RST ⇒ 仍然 `Answered`、黑洞地址 ⇒ `Silent`；真机
    `default_gateway()` 若返回则必须是非 0 非环回 IPv4）；
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
    Q34/Q42 存活集成测试：监听端口 ⇒ `Alive`、关闭端口 ⇒ `Refused`
    （`the_liveness_probe_is_a_bare_tcp_connect_to_the_control_port`，10 s
    超时避免把 RST 误读成超时）；`timeout_host_unreachable_and_network_unreachable_are_dead`
    走同一个 `liveness_from_error`；`Refused`/`Dead` + 健康媒体 ⇒ `Gone`/回收且
    任何 `statusFlags` 下都不是 busy；`an_unknown_chain_escalates_to_dead_and_one_ok_resets_it`
    用 `Tracker` 走完整条升级链；Q46 的四条：
    **`a_reachable_gateway_turns_the_all_failed_round_into_a_reclaim`**（网关
    `Answered` ⇒ `DevicesGone`/不抑制，两台设备的 `Dead` 照常生效并且都是
    `Gone`/`Reclaim`，绝不是 `BusyElsewhere`）、
    **`a_silent_gateway_suppresses_the_all_failed_round_and_marks_none_dead`**
    （网关 `Silent` ⇒ `LocalNetwork`/抑制，`LocalSuspect` ⇒ `Unknown`、计数为 0、
    `OursActive`/`FallThrough`；全拒连/单个失败/少探一台都不算全失败）、
    **`a_failed_gateway_probe_is_never_a_dead_receiver`**（真实黑洞地址 ⇒
    `Silent`；它只选择「本机网络」，不产生 `ReceiverLiveness`、不进 `Tracker`、
    不推进计数）、
    **`a_power_off_is_reclaimed_in_about_fifteen_seconds`**（假时间：网关每轮
    `Answered`，`Unknown` 链在 5 次 × 3 s = **12 s** 判死，落在 ≤ 15 s 预算内且
    不早于耐心上限）和
    **`a_roaming_client_is_not_declared_dead`**（4 次 `Unknown` 后 `Alive` ⇒ 不判死、
    计数清零、下一次抖动重新从 0 开始）；
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
    **`an_unreachable_control_port_reclaims_instead_of_a_generic_fault`**：真实关闭
    端口 ⇒ `gone`/回收、`tcp_reachable=false`、`liveness_verdict=dead`、原始
    `tcp_liveness` 是 `dead` 或 `refused`（取决于本机协议栈是回 RST 还是丢 SYN，
    两者动作相同）、不计 takeover、无 `preempted`；
    **`a_refused_control_port_is_logged_as_refused_and_never_as_dead`**：合成
    `Refused` 轮次 ⇒ probe/evidence 行都是 `tcp_liveness=refused`、
    `liveness_verdict=dead`、文案含 `refused`/`alive` 且**不含** `device is gone`、
    仍然回收；
    **`an_escalating_unknown_liveness_reclaims_after_the_patience_limit`**：健康
    媒体 + 连续 `Unknown`，前 4 轮 `None` 且每轮排 `LIVENESS_RETRY`，第 5 轮
    `Reclaimed`、`liveness_failures=LIVENESS_PATIENCE`、`liveness_verdict=dead`、
    不计 takeover；
    **`a_group_wide_liveness_failure_does_not_mark_the_device_dead`**：全失败且
    网关静默的轮次 ⇒ `None`、不中毒、计数不变、**但会排一轮 `LIVENESS_RETRY` 后的
    重采**（网关恢复必须能结束抑制），probe 行保留原始 `dead` 但
    `liveness_verdict=unknown`、`local_network_suspected=true`、文案含
    `local network`；**`the_local_network_warning_names_every_failed_probe_and_the_reference`**
    断言 `preemption_local_network` 的 `receivers`/`probes`/`verdict`/`gateway`/
    `gateway_probed`/`all_failed_for_ms`/文案（网关静默与拿不到网关两种）；
    新增 **`AllFailedEpisode`** 的四个：**`the_all_failed_episode_warns_once_and_re_arms_after_recovery`**
    （同一段抑制只发一条 warning，恢复后重新武装）、
    **`a_partial_round_never_resets_the_all_failed_episode`**（少探一台的轮次不
    重置 episode 时钟）、**`the_all_failed_fallback_expires_after_the_window_without_a_gateway`**
    （无默认路由时窗口内抑制、到 60 s 停止抑制）、
    **`a_reachable_gateway_makes_the_episode_reclaim_instead_of_warning`**（网关
    有回应时 episode 不抑制，设备照常判死）；
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
  - TCP 存活探测的真机错误码（Q42 已答复）：现在不再依赖枚举 —— 只有
    `TimedOut`/`HostUnreachable`/`NetworkUnreachable` 直接判 `Dead`，
    `ConnectionRefused` 判 `Refused`，其余一律 `Unknown` 并由「连续 5 次或 60 s」
    的升级计时器兜底，所以 Windows 具体返回哪个码都不影响「设备关机最终会被回收」。
    真机上关机/拔电/路由器隔离分别返回哪个码仍未实测（Q42 §四的 PowerShell
    四场景对照表还没跑），只是它不再决定行为。
  - 升级计时器在真机上的时间尺度：`LIVENESS_PATIENCE=5` × `LIVENESS_RETRY=3 s`
    ⇒ 约 12–14 s（Q46 要的 15 s 量级），这个节奏对真实 Wi-Fi 抖动（AP 漫游、
    跨 AP 重关联、DFS）是否仍会误回收，未在真机验证。注意这条只约束 `Unknown`
    链；真机断电如果直接返回 `HostUnreachable`（`Dead`）则**第一次探测就回收**，
    所以「15 s」是**判死上限**而不是延迟下限。如果发现误回收，优先调大
    `LIVENESS_PATIENCE`，`LIVENESS_RETRY` 保持 3 s（退避方案会破坏 15 s 预算）。
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
  - **Q46 网关参照物在真机上的未知数**：①家庭路由器对 TCP 443/80 是「回 RST」
    还是「静默丢包」未实测——静默丢包会被读成「本机网络」，于是「全组关机」不会
    被回收（假阴性，方向安全但可能烦人）；②多网卡/多默认路由时只按 `Metric1`
    选一条，Windows 实际还会叠加接口 metric，HDMI/虚拟网卡存在时可能选错，
    未验证；③VPN/代理/802.1X 环境下网关可能不答 ICMP/TCP，同样走「本机网络」；
    ④「拿不到默认路由 ⇒ 60 s 兜底」只在 `GetIpForwardTable` 返回空时触发，
    真机上什么时候会这样（Wi-Fi 已断开、IPv6-only）未实测。以上已记入
    QUESTIONS.md（Q48）。
  - **「全失败」不再永不回收，但抑制期间的代价变了**：网关静默时每
    `LIVENESS_RETRY` 会重采一轮（N 台设备各一次 `LIVENESS_TIMEOUT` 内的裸 TCP
    connect + 一次网关探测），用来让恢复的网关结束抑制、也让 60 s 兜底推进；
    这段时间音频循环会有阻塞（与原有「一轮 N × 4 s」同一量级），本地测试只覆盖
    假时间/模拟成员，未在真实双 HomePod 上测过时序。
  - 没有抓包验证 HomePod 被接管时 `/info` 响应是否仍可读、是否要求认证、
    `statusFlags` 是否在接管瞬间变化。
  - 两阶段轮询让一轮里所有成员**先探测再决策**：N 台设备最坏要等 N ×
    (`LIVENESS_TIMEOUT` + `PROBE_TIMEOUT`) = N × 4 s 才轮到最后一个成员的动作，
    但这个等待只在控制面已经出现证据时发生（正常会话不探测），且本地测试只覆盖
    单成员/模拟成员，未在真实双 HomePod 上测过时序。

## 还没做

| 项目 | 现状 | 备注 |
| --- | --- | --- |
| P2-12 热加入 | 未做 | 目前只做组创建与成员重试，运行中增删成员未实现 |
| P3-6 `bufferStream` | 未开始 | 报告建议暂缓：弱网才有收益，改造成本大 |
| P3-9 AAC-ELD / OPUS | 未开始 | 需要接收端能力探测与编码器验证 |
| P0-4 封面（`image/jpeg`） | 未做 | 尺寸/格式约束需要设备确认，见 Q10；失败也不能干扰音频 |
| P0-3 帧级 `interval_correction` | 未做 | 见上文，现用连续重采样代替 |
| 音量事件（音箱旋钮/遥控器） | 部分 | 事件里的 `volume` 已解析并转发，但还没写回设备音量状态 |

## 三项按决定暂缓（P1-10 / P3-4 / P3-5，未实现）

P1-10 延迟标定、P3-4 MRP 隧道、P3-5 经典 RAOP **已从上面「还没做」的待办表里
移出**：它们不是「下一轮就做」的普通条目，而是**按决定暂缓**。核心输入——真实
接收器的容忍度数据、MRP 的消息编号与加密封装、FairPlay 的密钥调度——在拿到硬件
或抓包之前无法获得；凭现有资料写实现，只能产出「能编译、能在本地跑、但不可能
在真机上成立」的代码，那比明确不做更糟。

仓库里**没有这三项的任何生产代码**：没有 `calibration.rs` / `mrp.rs` / `raop.rs`，
没有对应的 IPC 命令，也没有为它们新增依赖；`hello` 的 `capabilities` / `commands`
里同样没有它们。设计、可写/不可写的边界、接口占位、真机验收与前置清单见
[`DEFERRED-DESIGNS.md`](DEFERRED-DESIGNS.md)，其中接口一节全部标注为未实现占位，
不是现有行为。

表中其余条目（P2-12 热加入、P3-6 `bufferStream`、P3-9 AAC-ELD/OPUS、P0-4 封面、
P0-3 帧级修正、音量事件回写）不受这条影响，仍是「还没做」。

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

## P2-13b / P2-13c / P2-13d 组内时钟域（已完成，Q33 + Q39 修正）

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
  | 全部成员都没有 AirPlay 2 证据（bit40 且 `srcvers >= 354.54.6`） | **整组 NTP** |
  | 多成员且全部有 AP2 证据（HomePod 组 / Mixed Apple / 未知厂商 AP2 组） | **整组 PTP，无一例外** |
  | 成员之间 AP2 证据不一致（混组） | **拒绝**：`mixed_timing_group`，SETUP 之前失败（P2-13d） |

  「Apple TV 单机 = NTP」只在 `n == 1` 分支成立；一旦进多设备，成员的单机
  答案不再参与整组决策，Apple TV 必须作为 PTP follower 加入发送端的时钟域。
  这是 P2-13c 相对 P2-13b「组内取最保守」的修正：旧规则会把 Mixed Apple
  group 错误地压到 NTP，和 WAC 的 `n > 1` 分支相反。
- **混组是拒绝，不是降级（P2-13d，Q39）**：WAC 不会创建混组，三层独立守卫
  各挡一次 —— `:4410` 的 UI 选择（`selected.Select(d => d.SupportsAirPlay2)
  .Distinct().Count() > 1` 直接 return）、`:8316` `AddMember` 与 `:8404`
  批量设置（成员表都不让混）、后端再拒一次
  （`AirPlay 2 and classic RAOP cannot share one timing group` → UI 文案
  `Device_MixedTimingGroupUnsupported`「…Please connect them separately.」）。
  因此 `CreateTopologyPlan` 的 `Any(!SupportsAirPlay2)` 只可能覆盖「**全员
  非 AP2**」这一种情况，语义等价于 `All(!SupportsAirPlay2)`；它不是「混组
  降级」分支。
  AF 的对应实现：`profile::select_timing` 在成员 AP2 证据不一致时返回
  `Err(MixedTimingGroup)`（`TimingPlan::select` 原样上抛），`session.rs`
  的 `run_stream` 在全部 `/info` 探测之后、任何 SETUP 之前返回
  `Fault { code: "mixed_timing_group", channel: "setup", retryable: false }`，
  并通过 stderr 保留证据摘要。P2-13b 的「任一成员缺 AP2 → 整组 NTP」分支
  已删除：静默降级会让 AP2 成员丢掉 PTP/多房间同步而用户毫不知情，正是
  Q39 要求避免的「半个 AP2 会话」。单成员仍走 `solo_timing()`，行为不变。
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
- **组计划诊断只进日志（P2-13c / P2-13d）**：`group_timing_warning` 改名为
  `group_timing_diagnostic`，不再发 `{"event":"warning", ...}`，只把
  `group_timing_plan: <每个成员的 receiver_id/IP、是否 AP2、单机会选
  什么、显式请求什么>; one PTP/NTP clock domain for the group` 写到引擎
  stderr 日志。它解释的是「组计划如何压过成员各自的单机答案」，**不再描述
  任何整组降级** —— 混组在计划产生之前就被拒绝，根本走不到这条日志。桌面端
  `SessionController` 的 `warning` 通道因此完全看不到它，「SETUP 前
  `startedAt == 0` 被丢弃」的问题也随之消失。内容仍保留在日志与诊断导出里；
  一条组建立最多一行，不按成员或按包重复。
- **预判（`receiver_capabilities`）不变**：`choices()` 仍用
  `TimingPlan::single(fingerprint, codec)`，输入仍只有 `supportedFormats` +
  对端 `features`（没有 model/srcvers），IPC 的 `selected_timing` 输出没有
  变化。它是**单接收器**答案：一台 Apple TV 的预判仍显示 `ntp`，而实际加入
  多设备组时运行时会按组计划给它 PTP；组时钟不在这条 per-receiver IPC 响应
  里，统一在 `session::run_stream` 决定。代码注释已写明这个边界。
- **测试**：`session_simulation.rs` 的 mock 新增 model 覆盖、`timingPeerInfo`
  记录和「只探测不配对」（`pair = false`）模式；新增「Mixed HomePod +
  Apple TV 全 AP2 → 两人都 PTP，且 Apple TV 的 SETUP 带 timingPeerInfo」
  「全经典组 → 两人都 NTP」「混组 → `mixed_timing_group` 失败，两个 mock 在
  `/info` 之后没有收到任何请求（因此没有 SETUP）」三个握手级测试；原全 AP2
  HomePod 组测试保持。`tests/compat_profile.rs` 的 97 行表一行没改，另追加
  组规则（HomePod 组 / 通用 AP2 组 / Mixed Apple 组 / 全经典组 NTP / 混组
  拒绝 / 单机不变）与 `negotiate_planned` 的断言。Rust 全量：lib 204
  （203 passed + 1 ignored），main 2，集成测试 35 passed（soak 1 ignored），
  共 240 passed / 2 ignored。
- **没验证/没做的**：
  - 没有真机，也没有抓包确认：一台真实 Apple TV 加入 PTP 组后是否真的作为
    PTP follower 跟发送端时钟（`timingPeerInfo` 被接受、PTP offset 样本
    ≥30）；这是 Q33 之后最关键的未知项。
  - 没有真机体验过「混组被拒绝」：引擎在 `/info` 之后、SETUP 之前返回
    `mixed_timing_group`，桌面显示本地化文案；这条路径只有 localhost 模拟
    接收器覆盖，真实 HomePod + 老 AP1 设备混选时的界面表现未验证。
    （上一版记录的「AP1 + AP2 组整组 NTP 是否能出声」不再成立：这种组现在
    根本不会开始，不再是需要真机验证的兼容性问题。）
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
  | 引擎 `mixed_timing_group`（Q39：混组在 SETUP 之前被拒） | 混组拒绝 | AirPlay 2 与经典 AirPlay 1/RAOP 设备不能同组，请分开连接 |
  | setup 通道 + TXT 证明 classic-only（`NotAirPlay2`；组内混组会先命中 `mixed_timing_group`） | 仅经典 | AirFlash 暂不能推流 |
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

## §12.3 会话摘要（已完成，Q40/Q43/Q44 修订）

`native/airflash-engine/src/telemetry.rs`、`src/session.rs`、`src/main.rs`、
`desktop/AirFlash.Core/SessionController.cs`、`desktop/AirFlash.Core/DesktopSessionLog.cs`

- **一行一条**：每完成一次流式会话（正常停止、循环内故障、teardown），在既有
  IPC 事件流上输出**恰好一条** `{"event":"session_summary", ...}`，并同时向
  `%APPDATA%\AirFlash\logs\session-summary.csv` 追加一行（文件不存在时先写表头）。
  位置就是 WPF 的 `AppPaths.LogDirectory`，引擎侧沿用 `credentials.rs` 的
  `APPDATA\AirFlash` 约定，没有另造目录。
- **没有「重建次数」，只有「时间线因为什么被重置」**：Q40 的结论，Q43 把
  WAC 的 9 类扩成 **11 个桶**（WAC 的 9 个 + AF 自己的 `source_restart`、
  `sender_stall`），按原因**分桶，永不合并成单一数字**：
  `start, track_change, user_seek, receiver_rejected_flush, session_restart,
  confirmed_transport_failure, silence_resume, latency_change, flush,
  source_restart, sender_stall`。
  CSV 是 11 个扁平的 `timeline_resets_<reason>` 列（可直接 SUM），加一个
  `last_timeline_reset_reason` 字符串（`none` = 本会话没有重置）；JSON 事件里
  是 `timeline_resets` 对象加同一个字符串。完整 33 列（顺序即
  `telemetry::SUMMARY_COLUMNS` 的唯一声明）：
  `session_id, topology, codec, timing, applied_lead_ms, lead_shift_count,
  lead_shift_total_us, packets, rtx_req, rtx_resent, underruns, gap_filled,
  reanchored, stall_rtx_drained, stall_rtx_drained_max, servo_engagements,
  baseline_resets, mean_lateness_ms, p99_lateness_ms, max_lateness_ms,
  duration_s, timeline_resets_start, timeline_resets_track_change,
  timeline_resets_user_seek, timeline_resets_receiver_rejected_flush,
  timeline_resets_session_restart, timeline_resets_confirmed_transport_failure,
  timeline_resets_silence_resume, timeline_resets_latency_change,
  timeline_resets_flush, timeline_resets_source_restart,
  timeline_resets_sender_stall, last_timeline_reset_reason`。
  `rebuild_count` 已删除。11 个桶按枚举声明顺序排在末尾的
  `last_timeline_reset_reason` 之前；两个 lead 计数字段紧跟
  `applied_lead_ms`，因为它们描述同一套自适应 lead，不是重置桶；两个 stall
  严重度字段紧跟 `reanchored`：前者说卡了几次，它们说每次卡得多响。
  `track_change` / `user_seek` / `receiver_rejected_flush` / `flush` 在 AF
  的实时采集链路里没有站点，恒为 0，**保留作 tripwire**（Q43 §五）；`start`
  同样保留，但引擎不再产生它（见下）。
- **只有 `confirmed_transport_failure` 是漂移信号**：`track_change` /
  `user_seek` / `flush` / `silence_resume` 是中性或用户动作，`latency_change`
  是我们自己改 lead，`session_restart` / `receiver_rejected_flush` /
  `source_restart` 是各自的诊断，`sender_stall` 是本机侧错过 deadline。
  **`latency_change` 与 `silence_resume` 必须能区分**：前者来自
  `schedule.add_shift` 真正应用了 lead 变化（「我们主动干的」），后者来自
  standby 暂停后恢复、整条时间线重建（「静音后恢复」）；两者都不增加
  `confirmed_transport_failure`。
  **重锚不再无条件算传输故障**（Q43 §三，Q44 §一）：`LateAction::Reanchor`
  成功且 shift 非零时，发送线程错过 deadline 可能只是本机调度（GC/CPU 争用/
  磁盘抖动），也可能是网络真的坏了。Q43 最初的判据是「卡顿前 1 s 窗口里
  `rtx_req` 或 `underruns_active` **有任何上涨** ⇒ 传输故障」，但它太敏感：
  1 s 内一次无关的偶发丢包（Wi-Fi 上是常态）就会把本机卡顿判成网络故障，
  `confirmed_transport_failure` 会被噪声填满。现在的判据是**速率比**：
  - 预卡顿窗口仍是 **1 s**：发送循环每 250 ms 采样一次累计的 `rtx_req`
    （各成员 `retransmit_requests` 之和）与 `underruns_active`，重锚时取
    窗口内最旧的样本作 pre-stall 基线（`RtpEvidence`）；
  - 分母是该链路自己的**约 30 s 稳态速率**（同一份采样环扩到 30 s，从
    30 s 窗口内最旧的样本量到 pre-stall 基线为止，不把尖峰本身算进分母，
    否则 30 s 里的 1 s 尖峰会从 3× 被稀释成 2.8×）。一个一直丢 2 包/s 的
    网络，卡顿前 1 s 丢 2 包是**正常**，不是故障；
  - `transport_bad = (rtx_ratio > 3.0 && pre_stall_rtx >= 3) ||
    (underrun_ratio > 3.0 && pre_stall_underrun >= 1)`，其中
    `ratio = pre_stall / steady.max(0.05)`。0.05 是防除零的地板；3× 是
    **起点而不是定值**，等有足够多行数据后按分布调；
  - `underruns` 权重高于 `rtx_req`：rtx 只说明包丢了（大概率被重传救回来），
    underrun 说明播放缓冲真的饿了、已经能听见，所以绝对下限是 1 对 3；
  - 没有 pre-stall 基线（卡顿超过 1 s、第一次循环就卡住、或刚重锚完还在
    下一个采样间隔内）一律保守归 `sender_stall`；
  - 重锚只重置 pre-stall 窗口，**30 s 稳态样本保留**：一次卡顿不该丢掉
    「这条链路正常长什么样」的基线。卡顿期间积压、重锚后才被
    `service_retransmits` 收走的 RTX 请求因此不会算到下一次重锚头上。
  `reanchor` 预算耗尽（`timeline_expired`）仍是确定的
  `confirmed_transport_failure`。**这是启发式而不是证明**：卡顿前 1 s 内的
  无关丢包仍可能被算作传输证据，超过窗口的长卡顿没有基线；限制写在代码注释
  和 Q44/Q47。
  **`latency_change` 有 5 ms 门槛**（Q44 §二）：每次实际应用的 lead 变化都
  累加 `lead_shift_count` / `lead_shift_total_us`（微秒、带符号），但只有
  |shift| ≥ 5 ms 才记一次 `latency_change`。5 ms 就是 `continuity.rs` 的
  splice ramp 长度（`rate / 200` 帧，48 kHz 正好 5.0 ms、44.1 kHz 4.99 ms）：
  ≤ ramp 的调整被淡入淡出完全吸收，用户听不到，也不构成时间线事件。
  原来的 1 ms 门槛是错的——`set_lead` 只传整数毫秒，|shift| ≥ 1 ms **恒真**，
  `lead_shift_count` 与 `latency_change` 永远相等，两个字段的信息量完全
  重叠。现在前者回答「自适应环路调得多勤」，后者回答「有多少次调整大到
  可能被听见」。微秒精度保留，但只是内部不变量，不是产品能力（Q44 明确
  不做亚毫秒 lead）。
- **`stall_rtx_drained` / `stall_rtx_drained_max`：把「卡了几次」变成「卡得
  多响」**（Q44 §一）。每次成功重锚（即一次 stall）后，发送循环在同一个
  迭代里执行 `service_retransmits`，把卡顿期间积压的 RTX 请求收走；重锚时
  记下 `rtx_req`，drain 之后取增量累加到 `stall_rtx_drained`，并更新
  `stall_rtx_drained_max`（单次最大）。于是 `sender_stall > 0` 但
  drained ≈ 0 是**健康**的（接收端缓冲把卡顿吸收了）；少数几次卡顿但
  drained 很大才是可听的。`reanchored` 是次数，这两个是严重度。计数是
  下限：超过单次 `service_retransmits` 预算的积压会溢到后续迭代，不计入
  这次卡顿。
- **站点清单**（每一处都显式传 reason，不再有隐式计数）：
  - 会话首次 `MediaSchedule::new`：**不计**。它是这一行的基线，不是重置；
    正常停止必须 11 桶全 0（「没有重置」就是全 0）。`start` 因此保留但
    不产生，WAC 的「会话开始」语义由「这一行本身」表达。
  - `schedule.add_shift` 实际应用：每次都进 `lead_shift_count` /
    `lead_shift_total_us`；|shift| ≥ 5 ms 才记 `latency_change`。
  - `LateAction::Silence`（把错过的槽补成静音）：不计。seq/rtptime 仍线性，
    时间线没有重置。
  - `LateAction::Reanchor` 成功且 shift 非零：按 1 s 预卡顿窗口相对 30 s
    稳态的速率比分到 `confirmed_transport_failure`（明显尖峰）或
    `sender_stall`（背景丢包/干净/无基线）；同一迭代 drain 之后记录这次
    卡顿的 `stall_rtx_drained` / `_max`；reanchor 预算耗尽：
    `confirmed_transport_failure`（不记 stall 严重度，会话已结束）。
  - `schedule.relax`：不计。它是重锚 surplus 的慢速回退，再计一次会把一次
    卡顿放大成一串事件。
  - standby 暂停后 `run_live` 恢复：`silence_resume`。
  - 抢占 reclaim 后重开：`session_restart`。
  - 采集源结束/重建后重开：`source_restart`，**不是 `start`**（Q43 §四）。
    发送循环里发现 `source.ended()`、以及 `StreamEnd::SourceEnded` 之后
    重开采集，两处都走同一个 reason。
  - 正常停止的 faded drain：不计。它不是 FLUSH，接收器从未被要求丢弃已排队
    音频。
- **桌面端 companion 记录**：引擎看不到桌面重连。桌面端复用既有的
  `SessionController` 重连路径，在每次 IPC 会话结束时向
  `%APPDATA%\AirFlash\logs\session-desktop.csv` 追加一行
  `session_id,desktop_reconnects,transport_fault_first,transport_fault_count,transport_fault_seq`：
  - `session_id` = 发给引擎的 IPC session id（`Guid.NewGuid().ToString("N")`）；
  - `desktop_reconnects` = 这次 attempt 开始前，本次播放已经重连过几次（首次 0）；
  - `transport_fault_first` = 本次播放第一个导致重连的引擎 fault code，
    首次为 `none`；
  - `transport_fault_count` = 本次播放累计多少次重连故障（和
    `desktop_reconnects` 同一来源，二者不会互相矛盾）；
  - `transport_fault_seq` = 按发生顺序用 `;` 连接的 fault code，
    **只保留前 5 个，后面加 `…`**，避免重连循环把行撑大；没有故障时也是
    `none`。Q43 §五：旧的单值 `desktop_reconnect_reason` 只留第一条，
    丢掉了「重连 5 次、每次不同原因」这个最需要的故事。
  - **join key 不变**：引擎行的 `session_id` 是
    `<桌面 session_id>-<session_epoch>`（同一 IPC 会话里 standby 唤醒/抢占会
    再开一个 attempt）。把引擎 `session_id` 按最后一个 `-` 切开，前缀等于桌面
    `session_id` 即可 join；引擎一个 attempt 一行、桌面一次 IPC 会话一行，
    因此是 1:N。Q43 明确**不**为逐 attempt 对齐加 `session_epoch`：桌面端
    本来就没有 per-attempt 的 fault 记录，加了也配不上。两个文件同在
    `%APPDATA%\AirFlash\logs\`。
  - 只写不透明 session id 和固定 fault code 标签，不含 host/MAC/路径；I/O
    失败被吞掉，不影响会话。
- **全部来自既有计数器，没有为摘要新增音频路径计数**：
  - `session_id` = IPC 请求的 `session_id` + 本次流式尝试的 `session_epoch`
    （standby 唤醒/重连会产生新尝试，必须可区分）；由 `main.rs` 填入，
    不进线协议。
  - `topology` = 既有 `group_mode`（`single` / `native_stereo` /
    `software_stereo`；未设置的多成员回退 `group`）；`codec`/`timing` = 协商后
    的 `CompatProfile`，同组只有一个决定，取首个成员。
  - `packets` = `SessionTelemetry.packets`；`rtx_req`/`rtx_resent` = 各成员
    `MediaMetrics.retransmit_requests` / `retransmits_sent` 之和。
  - `underruns` 只算「有音乐时的欠载」；空闲保活欠载不是 dropout，不计入。
    `gap_filled`/`reanchored` = 调度器同名计数器。
  - `mean/p99/max_lateness_ms` = 既有 lateness 直方图（p99 取保守上沿，max 用
    `percentile_ms(1.0)`）。
  - `servo_engagements` = 采集伺服 engagement；`baseline_resets` 仍取
    `ServoCounters.steps`（断点后整环清零）——Q40 已确认这个映射正确，本轮
    不改。它不是 11 个 reason 桶之一，不会与它们重复计数。
  - `lead_shift_count` / `lead_shift_total_us` = `SessionTelemetry` 里新增的
    两个 lead 变化计数（次数、带符号微秒和），每次 `add_shift` 实际应用都
    增加；它们**不是**重置桶，只有 ≥ 5 ms 的那部分同时进
    `timeline_resets_latency_change`。
  - `stall_rtx_drained` / `stall_rtx_drained_max` = `SessionTelemetry` 的
    stall 严重度计数（累计 drain 的 RTX 请求数、单次最大），只由成功重锚
    站点更新；不是重置桶，也不会和 `reanchored` 重复计数。
  - `timeline_resets_*` = `SessionTelemetry.timeline_resets`（11 桶），只由
    上面站点清单里的显式 `record_timeline_reset` 增加；
    `last_timeline_reset_reason` = 最近一次的原因。`SessionTelemetry.expired`
    仍是内部计数器（`timeline_expired` 的症状），只是不再当列。
  - `duration_s` = 发送循环开始到写摘要的墙钟时长；`applied_lead_ms` = 调度器
    实际应用的 lead（内部按微秒累计，写出时取整到毫秒）。
- **脱敏与 best-effort**：JSON 先过 `telemetry::redact`（全是数字/枚举，形状
  不变）；CSV 只有数字、固定枚举和 `session_id`，不含 host/MAC/路径/密钥。
  写文件失败只 `eprintln!`（桌面端把引擎 stderr 收进日志），不返回错误，也不
  影响音频或控制路径；事件仍照发。桌面 companion 行同样只含不透明 id 和固定
  fault code 标签（以及截断后的序列），写失败被吞掉。
- **恰好一次**：摘要只有一个调用点——`send_packets` 的发送循环之后。故障从
  闭包返回后仍会走最终 metrics + 摘要，再做 TEARDOWN；最后一包的 drain 用
  闭包包住 `?`，避免它的错误跳过摘要。握手/建链阶段失败的尝试没有 session
  计数器，不产生摘要（这一行只描述真正开始流媒体的会话）。桌面行在每次 IPC
  会话结束的 `finally` 里写一次，重连决策发生在 `catch` 里，不会重复写。
- **测试**：正常停止 11 桶全 0 且恰好一条、注入 `Health` 故障后仍恰好一条且
  TEARDOWN 照常发生、已知计数逐字段对账（含 idle underrun 不计入）、11 个
  reason 各自独立可寻址且不泄漏到别的桶、`latency_change` /
  `silence_resume` / `source_restart` / `sender_stall` 都不增加
  `confirmed_transport_failure`、standby 恢复的 attempt 记
  `silence_resume`、采集源重开记 `source_restart` 且 `start` 保持 0、
  成功重锚按速率比分类（持续背景丢包 + 小幅增量 ⇒ `sender_stall`、明显
  尖峰 ⇒ `confirmed_transport_failure`、只有 1 次 underrun 也 ⇒
  `confirmed_transport_failure`、无基线 ⇒ `sender_stall`，四种都构造）、
  3 ms 的 lead 变化只加 `lead_shift_count`/`lead_shift_total_us` 而
  `latency_change` 保持 0、5 ms 与 7 ms 各记一次 `latency_change`、
  stall 严重度按累计/单次对账且无卡顿时为 0、CSV 33 列按声明顺序且无
  `rebuild_count`、桌面记录与引擎 `start` 使用的 session id 一致（引擎行再加
  `-<epoch>` 后缀）、桌面 fault 序列超过 5 条截断并保留计数、写文件失败不影响
  会话；telemetry 侧覆盖 JSON 字段/redact 形状、CSV 列序与无身份字段、追加时
  只写一次表头；桌面侧覆盖表头/转义/不可写路径。
- **验证**（合并 main 后重跑）：Rust lib 246 passed + 1 ignored、main 2、集成
  40 passed + 1 ignored，合计 288 passed / 2 ignored；clippy `-D warnings`
  无告警；`build-native.ps1 -Check` 通过；.NET 566 通过；pytest 141 通过；
  ruff 无问题。本轮没有复现已知偶发（`session_simulation.rs` 的 `AddrInUse`
  与 .NET standby 的事件时序空引用）；历史记录保留：
  `EngineStandbyReleasesMuteAndMetricsCannotOverrideState` 曾在首次全量时
  偶发一次事件时序空引用，单跑与全量重跑均通过。

## P3-2c 混组不可表示（桌面，已完成）

`desktop/AirFlash.Core/GroupMembership.cs`、`SoftwareStereo.cs`、
`GateFailure.cs`、`AirFlash.App/ViewModels/SettingsViewModel.cs`、
`AirFlash.App/Ui/SettingsWindow.xaml`

- **不是「连接前预检」，而是让混组不可表示**：新增 `GroupMembership`，组内
  成员只能通过唯一的 `TryAdd(Receiver, out string? error)` 进入；它拿已有
  第一个成员的 `Receiver.SupportsAirPlay2`（即
  `AirPlayRouting.SupportsAirPlay2`，TXT bit40 + `srcvers`）与来者比较，
  不同就拒绝、列表保持不变。第一个成员永远接受（WAC `Members.Count <= 0 ||`
  的形状），单机路径不经过它，行为不变。
- **一个谓词**：只比较路由布尔，不把四态展示标签塞回不变量（Q36/Q41）。
  `ClaimsAirPlay2Outdated` 在路由上仍是 classic，所以和 `NotAirPlay2` 一样
  触发拒绝；四态只用于决定是否追加原因句。
- **所有入口**：设置页左右接收器下拉（`SoftwareGroupEditor.LeftId/RightId`）
  在用户选中第二台设备的瞬间调用 `TryAdd`，失败时下拉回到原值并在卡片内
  显示文案；`AddGroup()` 创建组时用同一 `TryAdd` 枚举可用组合，只有所有
  组合都混组才报错；`SoftwareStereo.Resolve()`（会话恢复/目录解析）同样用
  `TryAdd` 重建已保存的组。`Swap()`、`ReceiverCatalog` 身份迁移只改槽位/
  身份，不改变成员集合，保持原路径。
- **两级文案**：动作句固定为已有的
  `AirPlay 2 and classic AirPlay 1/RAOP devices cannot be grouped together.
  Please connect them separately.`；冲突方中出现 `ClaimsAirPlay2Outdated`
  时**追加**一句点名设备的原因，不替换动作句：
  `「{0}」自称支持 AirPlay 2，但固件版本低于 354.54.6，已按经典设备处理。`
  英文 key 与 zh / zh-TW / de 三语都已加入；`GateFailure` 的
  `mixed_timing_group` 兜底也复用同一个组合器，所以引擎拒绝时也是两级信息。
- **立体声对：不照抄 WAC 的例外**。WAC 的 `!IsCustomStereoPair` 成立是因为
  自定义对通过 `SetStereoPair` 作为一个逻辑单元交给后端；AF 的软件立体声是
  两台独立接收器 + 左右声道/偏移，作为 `software_stereo` peers 发给引擎，
  没有 `SetStereoPair` 语义，引擎 `select_timing` 也照样拒绝混组。放行会
  构造出后端必然拒绝的组，所以不豁免；代码注释写明了这个取舍。原生
  HomePod 对是 mDNS `tsid` 的单一发现单元，不经过这条成员列表。
- **引擎兜底保留**：`native/airflash-engine/src/profile.rs::select_timing` 与
  `session.rs` 的 `mixed_timing_group` 未改动，仍是 CLI/测试/未来前端的
  权威拒绝。
- **测试**：classic 加入 AP2 集合失败且成员不变、AP2 加入 classic 集合对称
  失败、同类型成功、单成员永远接受、只有 `ClaimsAirPlay2Outdated` 追加原因
  且不替换动作句、软件立体声对不豁免、历史混组解析为离线空成员、门禁兜底
  文案复用组合器；既有 `GateFailure`/能力测试全过。
- **验证**：Rust 270 passed / 2 ignored（未改 native，数字与 main 一致）；
  clippy `-D warnings` 无告警；`build-native.ps1 -Check` 通过；.NET 566
  通过；pytest 141 通过；ruff 无问题。
- **没验证 / 待确认**：没有真机，无法验证真实 HomePod/Apple TV 的 TXT 是否
  会在运行中改变能力；升级前遗留混组的修复策略、以及缺少 TXT 证据（设备
  暂时离线）时严格布尔导致的提示语选择，见 Q45。
