# AirFlash 改造疑问清单（待 WinAirCast 实现/抓包确认）

> 用途：执行 [`report/10-改造路线图.md`](report/10-改造路线图.md) 时遇到的**拿不准、但报告里只给到字符串级证据**的问题。
> 每条都写成「我们暂定怎么做（可以继续推进）→ 需要你确认什么（参考 WinAirCast 的代码/行为）」，方便你拿着这张表逐条回答。
>
> 状态标记：`[待确认]` = 已按暂定方案落地但可能与 WAC 不一致；`[已确认]` = 你答复后回填；`[已否决]` = 你的答复推翻了我们的做法。
>
> 回答方式随意：直接在本文件对应条目的「答复」下写，或另起一份 `ANSWERS.md` 都可以。

---

## 一、时间轴重锚定（P0-1）

### Q1. `re-anchoring deadline one frame ahead` 到底动了什么？`[待确认]`

WAC 的日志只有一句：〔串〕`「Sender thread behind by <n> ms (beyond catch-up budget); re-anchoring deadline one frame ahead」`，没有说清"deadline"是本地发送节拍、还是 sync 包里的 `RTP → 网络时间` 映射。

三种可能，后果差别很大：

| 可能 | 做法 | 接收端看到什么 |
|---|---|---|
| **(a) 本地节拍平移** | 把本地 `schedule.start` 前移一帧，seq/rtptime 不跳 | 同一个 rtptime 对应新的网络时间；sync 包必须同步平移，否则时间轴断裂 |
| **(b) 推进时间轴槽位** | `frames += 跳过的包数 × 352`，`seq/rtptime` 一起前进（等价 AirFlash 现有的 `recover(live=true)` + `skip_packets`） | seq/rtptime 出现空洞（对端会发重传请求，补不上就自己遮盖） |
| **(c) 只改 lead** | 本地提前一帧发，`seq` 连续、sync 映射不变 | 时间轴完全不变，只是发送提前量变大 |

**我们暂时实现的是 (a)+(c) 的混合**（见 [`PROGRESS.md`](PROGRESS.md) 的 P0-1）：`MediaSchedule.shift` 平移整条时间线的起点，`seq` 与 `rtptime` 保持线性不动，`sync` 包里的 `rtptime → 网络时间` 锚点跟着一起平移（每个使用点都加 `anchor_shift_ns()`，包括原来作为局部变量的 `base_ns`），并留一帧相位余量。已明确否决 (b)：跳过 `seq` 会在接收端制造重复 nonce，也破坏「seq 连续」这条验收。**如果 WAC 实际是 (b)，这一项要重做**，所以仍待确认。

**需要确认**：

1. WAC 重锚之后，对端抓包能看到 `seq` 跳变吗？还是 `seq` 连续、只有 `rtptime` 与网络时间的关系变了？
2. 若是 (c)，WAC 是否会同时改 sync 包（`timeline()` 的 networkTime）？还是只把 `deadline` 提前、让 sync 保持原语义？
3. 重锚后 WAC 是否会把"跳过的音频"用静音包补齐（保持 seq 连续），还是让接收端自己发重传请求？

### Q2. catch-up budget 的具体数值和升级条件？`[待确认]`

AirFlash 现有阈值：`due > 8` 包（352 帧 @44.1k ≈ **63.8 ms**）或 `now + 2ms >= earliest_render` 即判 `timeline_expired`。

**我们暂定**：`due ∈ [2,8]` 补淡出静音（不变）；`due > 8` 或贴 render 截止线 → 重锚；**10 秒内重锚 3 次**仍追不上 → 才 `timeline_expired`。

**需要确认**：

1. WAC 的 catch-up budget 是 60 ms 还是别的（日志里的 `<n> ms` 是运行时值，抓一次日志就知道）？
2. "重锚也有上限"这条规则 WAC 有没有？上限是次数/时间窗，还是"连续 N 个包都没追上"？
3. 重锚时 WAC 会不会触发一次 `SETRATEANCHORTIME` 或重新 SETUP？

### Q3. 重锚与"源侧断点"的关系？`[待确认]`

WAC 会话状态机里有 `TrackBoundary` / `Reanchoring` 两个一等状态。AirFlash 没有"曲目边界"概念（永远是一条直播流）。

**我们暂定**：不引入 TrackBoundary；所有重锚都记 `timeline_reanchored` 计数 + 位移量。

**需要确认**：WAC 的 `Reanchoring` 状态是否只在 `TrackBoundary` 后出现？普通调度停顿用的也是这个状态吗（还是只有计数器）？

---

## 二、lateness 伺服与漂移（P0-3）

### Q4. servo 的输入量到底是什么？`[待确认]`

报告给的模板是〔串〕`「PTP timeline servo sample: lateness=<a>ms filtered_error=<b>ms correction=<c>」`。

我们理解 `lateness = 实际发送时刻 − 计划发送时刻`（正数=晚）。但也可能是"实际发送时刻 − 该包应在对端播放的时刻"（含 lead），二者差一个常量 lead，对滤波器影响不大，但对"步进判定 250 ms"影响很大。

**我们暂定**：`lateness = now − schedule.deadline()`（本地节拍偏差，单位 ms，正=晚），一阶低通 `filtered_error += 0.2 × (sample − filtered_error)`。

**需要确认**：

1. `lateness` 是"vs 本地计划发送时刻"还是"vs 对端播放时刻"？
2. `correction` 的单位是 ms 还是 samples？正号表示"提前发"还是"推迟发"？
3. `correction` 作用在什么上：(a) 发送提前量 lead、(b) 重采样比、(c) 帧插删、还是 (d) sync 包映射？

### Q5. 伺服常数：α、step 阈值、engage/release 门槛？`[待确认]`

报告从常数池推断：`0.2`（滤波系数）、`250 ms`（step 判定）、`5 ms`（最小修正粒度/engage 阈值），并明确标注这是**推断**。

**我们暂定**：`ALPHA=0.2`、`STEP_MS=250`（|sample| 超此值判断点、重置基线）、`ENGAGE_MS=5`、`RELEASE_MS=2`、修正速率上限 **1 ms/包**、`correction` 夹在 `[-40, +40] ms`。

**需要确认**：

1. 这些常数在 WAC 里能否确认（或从行为上观察：比如 5 ms 是不是"每次同步最多步进 5 ms"的 slew，而不是 engage 阈值）？
2. `correction` 的物理上限是多少？超出后 WAC 是重锚还是拆会话？

### Q6. `interval_correction=<n> frame(s)` 的量级与位置？`[待确认]`

报告写"每隔若干帧插/删一帧（22.7 µs），不改音高"。

**我们暂定**：在 PCM 域、编码之前，按累加器每攒够 1 帧（352 samples）的时间就插一帧或删一帧；插删点复用 `Splice::boundary()` 做过渡。

**需要确认**：

1. `<n>` 是"每 n 帧插删一次"（间隔），还是"本次插删 n 帧"（数量）？
2. 插删发生在编码前（PCM）还是编码后（ALAC 帧）？
3. 插删是否只对 PTP PCM 生效（WAC 的日志里写的是 `PTP PCM drift control`）？ALAC 路径走什么？

### Q7. 漂移合理性上限？`[待确认]`

〔串〕`「PTP drift estimate <n> ppb rejected as implausible (<reason>)」`。

**我们暂定**：`|ppb| > 1000 ppm`（=1e6 ppb）判无效，保留上一次估计；`reason` 用 `implausible`。

**需要确认**：WAC 的上限是多少（100 ppm？1000 ppm？），以及 `<reason>` 有哪些取值。

---

## 三、自适应 lead（P0-2）

### Q8. 运行中改 playout lead 的机制？`[待确认]`

WAC 有 `PROFILE_LATENCY <ms|auto>` / `CALIBRATION_LATENCY <ms|auto>` / `DEV_PTP_LATENCY`，说明 lead 可以**运行中**改（不必重连）。

**我们暂定**：引擎新增 `set_lead { ms, slew_ms }`；同时平移"本地发送 deadline"和"sync 包的 networkTime"，每次同步步进 ≤5 ms（slew），保证 seq↔rtptime↔网络时间仍线性。

**需要确认**：

1. WAC 改 lead 是改 sync 包映射（"Locked PTP lead gliding on the wire"）还是重新 SETUP 改 `latencyMin`？
2. 会不会触发对端重缓冲/咔哒？WAC 的 slew 步长是多少（日志里的 `slew=5` 是不是"每次同步最多 5 ms"）？
3. `latencyMax` 在 lead 变化时要不要跟着改（WAC 常态是 `latencyMin + 100 ms`）？

### Q9. 候选阶梯与 floor 的实际生效方式？`[待确认]`

报告给的阶梯（HomePod solo 50/40/30、floor 50；HomePod group 120/110/100/90；含原生立体声对 ≥120 等）来自 `LatencyCalibrationPolicy` 的**候选列表**，但报告同时说 `VerifyRules`/`GradeBProbePlanner` 是死代码，生产线用的是 `IsHealthy` + `ComputeRecommendation`。

**我们暂定**：把候选阶梯直接写成表，用 8 s 遥测窗口做归因（`SourceGlitch` 不升档），升降档用 slew 滑过去。

**需要确认**：

1. 生产线的 `ComputeRecommendation` 到底按什么规则升/降档（步长 10 ms？直接跳到下一候选？）
2. `SoloHomePodAlacValidatedFloorMs=50` 是"低于 50 就直接拒绝"还是"低于 50 仍按用户要求应用、只打日志"（日志写的是 `applying as requested`）？
3. 声学标定（−250…+250 ms）报告说"当前停用"，我们是否也先不做？

---

## 四、元数据与反向控制（P0-4 / P3-1）

### Q10. `SET_PARAMETER` 的三种格式，HomePod 认哪个？`[待确认]`

报告列了：`progress: start/curr/end`（text/parameters）、`application/x-dmap-tagged`（DMAP TLV）、`image/jpeg`（封面）。

**我们暂定**：三种都发（先 progress，再 DMAP 曲目信息，再封面），节流：进度变化 ≥1.5 s 或状态变化时发。

**需要确认**：

1. HomePod（AirPlay 2 + PTP）实际接受哪种？text/parameters 的 `progress` 单位是毫秒还是采样？
2. 曲目信息是 DMAP 更稳还是 text/parameters 的 `title:/artist:/album:` 更稳？HomePod 对两者反应如何？
3. 封面是否必须 `image/jpeg`、≤2048 px？空 body 能不能清封面？
4. 这些 `SET_PARAMETER` 是发在 **control 连接**（RTSP）还是 **event 连接**上？AirFlash 目前只有 control 连接可用。

### Q11. 事件通道的 plist 键名与取值？`[待确认]`

**我们暂定**：按 `mrCommand` → `command` → `cmd` 的优先级取命令；`action == "volume"` 时取 `value: real`。

**需要确认**：

1. `mrCommand` 的真实取值是字符串（`"play"`/`"pause"`）还是位掩码数字？WAC 的映射表在哪？
2. 音量事件的 `value` 是 0..1、-144..0 dB，还是 0..100？
3. 是否存在 `type`/`options` 字段需要一起解析（报告提到 `options`/`arguments`）？

### Q12. SMTC 侧的去抖与进度策略？`[待确认]`

报告：2 s 轮询 + 300 ms 去抖 + 进度策略（Δ≥1500 ms / rate 变化 / duration 变化）。

**我们暂定**：C# 侧 `NowPlayingService` 用 2 s 轮询 + 300 ms 去抖；进度变化 ≥1500 ms 或播放状态变化才下发。

**需要确认**：300 ms 去抖是"事件触发后等待 300 ms 再读"还是"两次相同事件间隔 300 ms 才算稳定"？封面转码是"超过 4 MiB 或边长 >2048 才转"还是"一律压到 2048"？

---

## 五、采集侧（P1-7）

### Q13. "这一包是否延续上一包"的判据？`[待确认]`

〔串〕`「WASAPI discontinuity flag on a packet that continues the previous one; ignored (occurrence <n>)」`。

**我们暂定**：记录上一包的 `IAudioCaptureClient::GetBuffer` 的 `pu64DevicePosition`（或 `pu64QPCPosition`）+ 帧数；若本包 `position == last_position + last_frames`（允许 0 误差）且 `qpc` 与预期偏差 < 1 个设备周期，则判"延续"，忽略 `DATA_DISCONTINUITY` 并计数。

**需要确认**：

1. WAC 用的是 device position 还是 qpc 连续性？
2. 容忍窗口多大（严格相等？±1 帧？±1 个 engine period？）
3. 判为"真断点"后，WAC 是清队列 + `reset()`，还是只清队列不 reset 重采样器？

### Q14. dither 的实现与静音处理？`[待确认]`

WAC 日志：`「… -> <rate> Hz/16-bit dithered, <x> ms filter」`。

**我们暂定**：TPDF dither（±0.5 LSB）+ 一阶误差反馈整形；**全零帧不加 dither**（否则静音段会多出 −90 dBFS 噪声，且会破坏 AirFlash 的"静音包=精确 0"语义）。

**需要确认**：

1. WAC 的 dither 是 TPDF 还是矩形？位深是恒定 16 bit 吗？
2. 全零帧（静音）也会抖吗？WAC 的"空闲 keepalive 直接静音"是精确 0 还是带抖动？
3. `<x> ms filter` 是抗混叠滤波器的长度吗（比如 2 ms）？

### Q15. 进程级捕获必须自转格式吗？`[待确认]`

WAC 的进程回环走 `INCLUDE_TARGET_PROCESS_TREE`，AirFlash 现在是 `AUTOCONVERTPCM` + 显式 S16 格式。

**我们暂定**：改为"先取 mix format 自转，失败再回落到 AUTOCONVERTPCM"。

**需要确认**：WAC 的进程回环是否真的用 `AUTOCONVERTPCM`？它怎么处理"进程回环的 mix format 是浮点 48 kHz"这种情况？

---

## 六、控制面与时钟（P1-9 / P1-6）

### Q16. control EOF 之后的重建策略？`[待确认]`

〔串〕`「Control EOF policy note: … single Apple TV reconnects the member, multi-device preserves shared RTP unless media fails」`。

**我们暂定**：feedback/event 连续失败时只记遥测并尝试重建（有限次），RTP 仍在发包时**不**拆会话；只有媒体面持续失败（12 s 无成功发送）或确认源静音才拆。

**需要确认**：

1. 单 Apple TV 场景"reconnects the member"是重建 RTSP（TEARDOWN+SETUP）还是只重连 feedback？重建期间 RTP 停不停？
2. 多设备场景 control 永久丢失后，WAC 什么时候才拆？（源静音？用户停止？）
3. `clock_lost`（PTP 失步）WAC 是"用 last-known offset 继续"多久才拆？

### Q17. BMCA 的比较键与报文形式？`[待确认]`

**我们暂定**：用 `(priority1, clockClass, priority2, clockIdentity)` 字典序比较；只处理**单播** Announce（AirPlay 的 PTP 是单播 profile）；超时 3 个 Announce 周期（3 s）回 Listening。

**需要确认**：

1. WAC 的 Announce 是单播还是组播（224.0.1.129 / 802.1AS 的 01:80:C2:00:00:0E）？
2. 比较键是否包含 `stepsRemoved` 和 `grandmasterIdentity`？`priority2` 用到了吗？
3. Apple TV 家庭影院组里，WAC 真的会 yield 吗？yield 之后它是否还发 Announce（作为 slave）？

### Q18. 三件与抓包相关的小事 `[待确认]`

1. WAC 的 `RTP-Info` / `FLUSH` 双版本语义（`as in 2.1.5.0`）——有没有设备真的需要旧语义？AirFlash 需要保留哪些兼容分支？
2. `[Status] PREEMPTED` 的触发信号是什么（403？`Connection: close`？设备重新 announce？）
3. `AirPlay 2 预判`用 `features bit40 && srcvers >= 354.54.6`，而 AirFlash 用 `features bit41` 判 PTP。这两个 bit 的准确语义是什么？是否应该同时用？

---

## 七、优先级与取舍（需要你拍板）

### Q19. 哪些项**不做**？`[待确认]`

报告 [11-不该照抄](report/11-不该照抄.md) 已经排除了一批。剩下这些我们倾向"先不做"，请确认：

| 项 | 我们不做的理由 |
|---|---|
| 经典 RAOP / 初代 AirPlay（P3-5） | 工作量≈现有 AirPlay 2 路径的一半，且需要 FairPlay；引擎侧零基础 |
| `bufferStream` 缓冲流（P3-6） | 报告自己说"收益/成本比偏低，建议暂缓" |
| 声学标定向导（P1-10） | 依赖真实设备测量；先做"遥测 + 归因 + 自适应"，标定后置 |
| 云诊断上报（P2-14 的一半） | 开源项目不做后台上报；只做本地 `diag_summary` 导出 |
| AAC-ELD / OPUS（P3-9） | 只有"延迟压进 40 ms"才有意义，而 40 ms 梯队要 lead 全套配合 |

**需要确认**：这份"不做清单"是否同意？如果不同意，哪一项要提高优先级？

### Q20. 验收口径 `[待确认]`

报告建议每次会话结束输出一行 `session_id, topology, codec, timing, applied_lead_ms, packets, rtx_req, rtx_resent, underruns, gap_filled, reanchored, servo_engagements, mean_lateness_ms, p99_lateness_ms, duration_s`。

**我们暂定**：AirFlash 已有 `transport_metrics`（1 s 一次）和会话结束的 `metrics` 事件，把上面这些字段加进去，并在 C# 侧写进日志。

**需要确认**：这条"会话行"是写到引擎 stdout（IPC 事件）、Windows 事件日志，还是 C# 日志文件？WAC 是写到哪个诊断桶里？

---

## 八、第二轮落地时新增的疑问

> 下面这些是在 P0-3 / P1-7 / P1-9 / P0-2 / P3-1 实现过程中新产生的；都已按「暂定做法」落地，改起来成本不高，但最好一次对齐。

### Q21. 有没有「运行中改提前量」的原生机制？`[待确认]`

我们新增了 IPC `set_lead { ms }`：把请求值作用到 `MediaSchedule.shift`，同步锚点一起移动，**不重建会话**；人工设定的值不受 `relax()` 每秒回收一帧的影响。上限 2000 ms。

**需要确认**：

1. WAC 调整 lead 时是改 sync 映射、改 deadline、还是重发 `SETRATEANCHORTIME`？
2. 运行中改 lead，接收端会不会出现「同一段音频被播两次」或「跳一段」的可听后果？WAC 是否只在会话边界改？
3. 2000 ms 的上限是否合理，还是应当按接收端 buffer 能力动态取值？

### Q22. 自适应阶梯的具体数值 `[待确认]`

`LatencyPolicy` 目前用：单只 HomePod ALAC/PTP 起点/下限 50、阶梯 50/60/80/120/200；PCM/PTP 起点 60、下限 45；NTP 起点 200、下限 120；立体声对（原生/软件）起点=下限 120。上调条件是「一分钟内 3 次时间线恢复」，每分钟最多一级。

**需要确认**：

1. 报告里的「50/40/30 阶梯，验证下限 50」自相矛盾（下限 50 就到不了 40/30）。到底是**起点 50 只能往上**，还是**下限 30、50 只是推荐起点**？
2. 立体声对 120 ms 的下限是硬结论还是保守估计？有没有更小的已验证值？
3. 上调判据用「恢复次数」对不对？WAC 是用欠载次数、p99 迟到，还是重传率？

### Q23. 16 bit 输出要不要抖动？`[待确认]`

我们加了 TPDF 抖动（±1 LSB），但**数字静音保持精确 0**（接收端遮盖逻辑依赖全零帧判定）。

**需要确认**：WAC 是否也抖动？如果它不抖动，我们和它的听感会有细微差异（我们的本底噪声高约 1 bit，但没有量化失真）。若 WAC 用的是噪声整形（error feedback），请给出阶数/系数。

### Q24. `DATA_DISCONTINUITY` 怎么判真伪？`[待确认]`

我们的做法：记录上一包的设备位置 `position + frames`，若本包 `position` 与之相等就判为「回环绕回」的伪标志，只计数不清队列；否则才是真断流（清队列 + 重置重采样器 + 打 splice 边界）。

**需要确认**：

1. WAC 用 `pu64DevicePosition` 连续性判，还是用 QPC 时间差 / 包间隔判？
2. 真断流时 WAC 会不会保留重采样器状态（避免滤波器瞬态），只打 splice 标记？我们现在是 `reset()`，可能有一下轻微「咔」。

### Q25. 事件通道 body 的准确形状 `[待确认]`

我们的解析器按 `mrCommand` → `command` → `cmd` → `action` 顺序找键，字符串形式（`play`/`pause`/`toggle`/`next`/`previous`/`mute`/`volume_up`…）与 MRP 数字（0 Play、1 Pause、2 Toggle、3 Stop、4 Next、5 Previous、6 Shuffle、7 Repeat、29 Mute、30 VolUp、31 VolDown）都接受；`volume` 支持 0–1、0–100、dBFS 三种归一化。整个事件以 `remote_command` 转发给桌面端。

**需要确认**：

1. HomePod 按暂停时实际发的是哪个键、什么类型（整数还是字符串）？body 是单个 dict 还是数组？
2. 数字编码是否就是 MediaRemote 的枚举（0=Play…）？我们用的映射表对不对？
3. `volume` 的线上单位到底是 0–1 还是 dBFS？AirFlash 现在只有「设备音量 SET_PARAMETER」，没处理过事件里的音量。
4. 我们只是把命令转发给桌面端、由 C# 去驱动系统媒体控制。WAC 是直接调 MediaRemote/SMTC 还是自己合成键？如果它用 `SendInput` 发 `VK_MEDIA_PLAY_PAUSE`，我们照抄更省事。

### Q26. QoS 标记用哪条路？`[待确认]`

我们要给音频 UDP socket 打 DSCP EF（46），两条路：

| 方案 | 优点 | 代价 |
| --- | --- | --- |
| `setsockopt(IPPROTO_IP, IP_TOS, 0xB8)` | 一行、无状态 | Windows 上常被忽略；只能标 socket，不分流向 |
| qWAVE（`QOSCreateHandle` + `QOSAddSocketToFlow`） | Wi-Fi 上真能拿到 WMM 优先级 | 需要 handle 生命周期管理，失败时要能优雅退化 |

**我们暂定**先做 `setsockopt` 版本（best-effort，失败只记遥测）。

**需要确认**：WAC 用的是 qWAVE 还是 `IP_TOS`？如果 qWAVE，`QOSFlowRate` / `ServiceType` 之类的参数它怎么填？

## 答复区

> 在这里写你的答复，或新建 `ANSWERS.md` 引用编号即可。我们会在下一轮把结论回填到上面对应条目，并同步修改实现。

（待填）
