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

## P3-3 抢占识别（按 WAC 模型返工，已完成）

`native/airflash-engine/src/preemption.rs`（重写）、`rtsp.rs`、`transport.rs`、
`session.rs`、桌面状态与文案

- **为什么返工**：上一版把「未预期 TEARDOWN / RTSP 403 / `Connection: close`」
  当信号，再要求媒体停流或 PTP association 改变作旁证，才判 `preempted`；
  没有旁证就退回通用故障。Q30 的答复指出这个方向反了：WAC 的权威判据是
  **live `GET /info` 的 `statusFlags`**，控制面动作只是带上下文的证据，而且
  **探测失败/超时反而照样回收**（fail-open）。原实现会把「接收器已被别人占用、
  但本地媒体尚未停流」误判成普通网络故障，也会在探测不到时错过回收时机。
- **唯一判据是 live probe**：新增 `ProbeOutcome { Free, BusyElsewhere, Unknown }`，
  `probe()` / `probe_report()` 在**新开的 RTSP 连接**上发 `GET /info`，复用
  `rtsp::Connection` 与二进制 plist 解析，不引入第二个 HTTP 客户端；默认超时
  2 s，超时、连接失败、RTSP 错误、plist 错误都返回 `Err`。控制面三条线索
  （未预期 TEARDOWN、403、`Connection: close`）只触发探测，不再参与判定。
- **判决表**（`action_for` / `action_for_result`）逐条对应 Q30：
  | 探测结果 | 动作 |
  | --- | --- |
  | `Free` | 立即回收 |
  | `BusyElsewhere` | 3 s 后再探（排队，不是失败、不结束会话） |
  | `Unknown` / `Err` / 超时 | 照样回收（fail-open，绝不落入通用故障分支） |
- **位定义未知时的安全降级**：`statusFlags` 的位映射在 Q30 §6 里是「静态不可判定」。
  只保留一个解释函数 `another_sender(flags) -> bool`，常量
  `ANOTHER_SENDER_BIT: Option<u32> = None`。只要它还是 `None`，
  `another_sender` 恒为 `false`，任何格式正确的 `statusFlags` 都只能得到
  `Free`，绝不会产生 `BusyElsewhere`，也就不会因为猜错 bit 而永久排队；
  原始 `statusFlags` 会原样写进 `preemption_probe` 日志，供真机对照。
  单测用 0、1、`1<<31`、`0x8000_0000`、`u32::MAX` 等位图断言只可能是 `Free`。
- **连续计数 + 时间窗 + yielding**：`TakeoverCounter` 按接收器（IP）计数，
  每个 live 播放进程跨 reclaim 复用。`TAKEOVER_LIMIT=3`、
  `TAKEOVER_WINDOW=30s`、`REPROBE_DELAY=3s`、`PROBE_TIMEOUT=2s` 都是**我们选的
  值**，不是从 WAC 静态恢复的常数，注释里已标明（Q31 答复建议 re-probe 取
  3–5 s，这里取了下限）。窗口内再次 takeover 计数
  +1；超过窗口重置为 **1**（不是 0）；达到上限不再回收，返回不可重试的
  `preempted` fault，`reason="limit"`，warning 也是 `reason="limit"`。单次
  正常接管走 `reason="takeover"`；两种 reason 同时出现在 warning、fault 和
  桌面文案里，用户能区分「被接管」与「反复被接管后让步」。
- **回收在引擎内部完成**：live 循环捕获专用的 `preemption::Reclaim` 错误，
  **不把它当故障上报**；旧 `Member` 被丢弃，`run_live` 用新连接重新
  `run_stream`，`TakeoverCounters` 跨轮保留。有限时长探测没有 live 循环，
  捕获 `Reclaim` 后转成终态 `PREEMPTED(takeover)`。这样桌面不需要新增自动
  重连逻辑，正常会话行为也不变。
- **旧连接必须中毒**：回收前 `Connection::poison()`，之后任何 `write` /
  `finish_session` 都返回 `WireError::Poisoned`，不会把 TEARDOWN 或新请求
  发回已被接管的会话；回收只能走新连接（与 Q16 的「加密 RTSP 不能中途
  re-key」一致）。`run_live` 同时 `discard_stale()`，避免旧 capture backlog
  灌进新会话。
- **保留的边界**：我们自己发起的 `TEARDOWN` 仍不算抢占；`401` 仍是
  `authentication_failed`，`454` 仍是 `session_rejected`；`403` 降级为证据
  （`forbidden` warning/fault 可重试），不再冒充认证失败。`preempted` 的
  `Member::close()` 仍不回发 TEARDOWN。
- **不会形成重连风暴**：一个 live 循环里每个探测周期最多回收一次；busy 时
  只排下一次探测，不重建会话；连续计数到上限立即让步。正常会话不探测，
  只有控制面出现证据后才发一次 `/info`。
- 测试：
  - `preemption.rs` 表驱动单测：判决表六种组合、`Err` fail-open、未知位图
    只可能 `Free`、缺字段/类型错误为 `Unknown`、原始 flags 保留、首
    takeover 计 1、窗口内递增、窗口外重置为 1、到上限 yielding、按 IP
    独立计数、busy 排队不缩短间隔。
  - `tests/preemption_probe.rs`：localhost `/info` 服务器，验证两次探测各开
    一条新连接、`GET /info`、任意未知位图都是 `Free`、无 `statusFlags`
    为 `Unknown`、连接拒绝/静默超时为 `Err` 且判决为回收。
  - `tests/rtsp_contract.rs`：中毒连接不写出任何请求，`finish_session` 也
    不写 TEARDOWN。
  - `session.rs`：假接收器主动发 TEARDOWN 后，完整 media 循环走
    `PreemptionPoll::Reclaimed`，断言 `preempted`、旧连接中毒、warning
    `reason=takeover`、证据行带 `policy`/`rtp_healthy`/`packets_prepared`、
    probe 行带原始 `statusFlags`、没有回发 TEARDOWN；另有缺字段、探测失败、
    连续上限三条路径。
  - 桌面 `IpcFailureTests`：`reason=limit` 使用「反复接管后让步」文案，且
    仍是 `PlaybackState.Preempted`、不自动重连。
- **没做/没验证**：
  - Q31 答复从 WAC 里恢复了 `ReceiverSessionActive = 0x20000`（字段本身是
    64 位 `ulong`），但这个 bit 表示「接收器上有**任意**活动会话」，包括我们
    自己的；要区分「另一个 sender」还需要 `we_hold_session` 这一维，而 AF 在
    探测时仍自认为持有会话，控制面症状也不足以可靠地回答这个问题。所以
    `ANOTHER_SENDER_BIT` 仍是 `None`：所有格式正确的 `statusFlags` 都按
    `Free` 处理，不会误回收自己的会话，也不会永久排队。当前解析只取低 32 位；
    若真机返回高位值，`u32::try_from` 失败会得到 `Unknown` 并 fail-open 回收。
  - `TAKEOVER_LIMIT`、`TAKEOVER_WINDOW`、`REPROBE_DELAY`、`PROBE_TIMEOUT`
    均为本地取值，未与 WAC 的运行时参数核对；Q31 还建议回收前隔 500 ms 再
    确认一次 `Free`，本次没有实现，仍按 Q30 的单次探测决策。
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

## P2-13b 组内时钟域（已完成）

`native/airflash-engine/src/profile.rs`、`src/session.rs`、
`src/receiver_capabilities.rs`、`src/session_simulation.rs`、
`tests/compat_profile.rs`

- **缺陷**：`Member::connect()` 在每个成员自己的 `/info` 之后各调一次
  `CompatProfile::derive()`（内部是单成员协商），于是立体声对的两个成员可能
  一个拿到 PTP、一个拿到 NTP。实际触发是「一个成员有 bit40、另一个没有」：
  有 bit40 的按 AP2 走 PTP，没有的退回 NTP，同一组出现两个时钟域。WAC 的
  `CreateTopologyPlan` 不是这样做的，它先汇总整组再给统一结论。
- **整组规则**（`profile::select_timing`）：
  - 空组 → NTP（不会发生，防御性默认）；
  - 单成员 → 完全沿用 97 行表刻画的单机行为（`solo_timing()`），PTP/NTP 不变；
  - 多成员：任一成员没有 AirPlay 2 证据（bit40 且
    `srcvers >= 354.54.6`）→ **整组 NTP**，这是本次修的 bug；
  - 都在 AP2 分支内 → 取成员中最保守的结论：任一成员单机应为 NTP
    （例如 solo Apple TV）→ 整组 NTP；否则整组 PTP。一个组只允许一个时钟域。
- **只算一次、所有成员共用**：新增 `TimingEvidence`、`TimingPlan` 与
  `CompatFingerprint::negotiate_planned(plan, …)`。`session.rs` 的 `run_stream`
  改成两阶段：先对全部成员做 `/info` 探测（失败仍按原逻辑重试一次），用整组
  指纹算 `select_timing`，再给每个成员用同一个 `TimingPlan` 做 SETUP；成员
  自己的 `profile.timing` 不再能制造分裂——显式 NTP 请求会在 SETUP 前被折进
  组计划（组整体 NTP），组计划 NTP 时显式 PTP 被降级（WAC 的整组降级），
  `Transport::Auto` 在组 NTP 下只能选 Realtime（buffered 是 PTP-only），显式
  Buffered 或 `follow_receiver_clock` 在组 NTP 下直接报错，而不是悄悄开第二
  个时钟域。单成员走 `TimingPlan::single`，`Timing::Inherit` 仍用
  `inherited_ptp`，行为与改造前一致。
- **共享决策函数**：`receiver_capabilities::choices()` 也改为
  `TimingPlan::single(fingerprint, codec)` + `negotiate_planned()`，预判与运行时
  调用同一个 `select_timing`。预判输入仍只有 `supportedFormats` + 对端
  `features`（没有 model/srcvers），因此 IPC 的 `selected_timing` 输出没有变化，
  97 行表全绿。
- **分歧告警**：组 SETUP 前若成员指纹/请求不一致，只发一条
  `{"event":"warning","code":"group_timing_divergence","channel":"setup",
  "host":<起因成员>,"recovered":true,"message":"…"}`，消息里列出每个成员的
  `receiver_id`（缺省用 IP）、是否 AirPlay 2、单独会选什么、用户请求什么，
  以及最终强制的 PTP/NTP。一次组建立只发一次，不在每个成员或每个包重复。
  注意：桌面端 `SessionController` 会丢弃 `startedAt == 0` 之前的 warning，
  这条告警在真实 UI 上是否可见没有验证（见下）。
- **测试**：`session_simulation.rs` 的 mock 新增 `ap2` 开关（bit40 +
  `sourceVersion 366.0` + `AudioAccessory5,1`）并记录每次 SETUP 的
  `timingProtocol`；新增「全 AP2 → 两人都 PTP」「一人缺 bit40 → 两人都 NTP，
  且告警恰好一次并点名两个成员」两个握手级测试；原来刻意跑两个时钟域的
  mixed-transport 测试改为同一 PTP 时钟域（第二个成员也接一个假 PTP
  responder，`SETPEERS` 后方法序列相应变为 4 项），传输仍保持 buffered+realtime
  混合。`tests/compat_profile.rs` 在 97 行之外追加组规则与
  `negotiate_planned` 的断言。Rust 全量：lib 181 passed + 1 ignored，main 2，
  集成测试 33（其中 soak 1 ignored），共 216 passed / 2 ignored。
- **没验证/没做的**：
  - 没有真机，也没有抓包确认「AP1 + AP2 组」在真实接收器上接受整组 NTP；
    老 HomePod 对 PTP 的偏好只在模拟器里成立。
  - 混合 HomePod + Apple TV 的组按本次「保守取 NTP」实现；WAC 报告里
    Mixed Apple group 是 PTP。两者不一致，记入 Q33 待答复。
  - `/info` 的 `name` 仅作为 model 缺失/为 `Unknown` 时的兜底；没有真实
    `/info` 样本证明各型号的 `name` 字符串，也没有真机验证。
  - 组 NTP 下 `follow_receiver_clock` 直接失败（无法既跟随接收器 PTP 又整组
    NTP），UI 目前只按用户设置下发，未做提前禁用/提示。
  - 桌面端可能丢弃 SETUP 前（`startedAt == 0`）的这条 warning，实际 UI 是否
    展示未验证；若要保证可见，需要桌面端放宽 warning 过滤或延后到
    `streaming` 后重发，本次未改桌面端。
