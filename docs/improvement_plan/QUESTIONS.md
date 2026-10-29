# AirFlash 改造疑问清单（待 WinAirCast 实现/抓包确认）

> 用途：执行 [`report/10-改造路线图.md`](report/10-改造路线图.md) 时遇到的**拿不准、但报告里只给到字符串级证据**的问题。
> 每条都写成「我们暂定怎么做（可以继续推进）→ 需要你确认什么（参考 WinAirCast 的代码/行为）」，方便你拿着这张表逐条回答。
>
> 状态标记：`[待确认]` = 待答复；`[已确认]` = 已核实（结论可照做）；`[部分确认]` = 部分核实（关键数值或内部细节无法静态确定，已给出上界/推荐值/验证方法）；`[已否决]` = **你们的做法需要改**。
>
> **本轮答复的证据来源**：`winaircast-backend.exe` 的反汇编（rizin，地址均为 VA）、二进制内的字符串池（〔串〕）、以及 `WinAirCast.decompiled.cs`（ILSpy，行号即文件行号）。〔析〕= 反汇编确认；〔串〕= 原文字符串。〔推〕= 推断。
>
> 回答方式随意：直接在本文件对应条目的「答复」下写，或另起一份 `ANSWERS.md` 都可以。

---

## 一、时间轴重锚定（P0-1）

### Q1. `re-anchoring deadline one frame ahead` 到底动了什么？ `[已确认]`

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

> **答复**：**是 (a)+(c)，你们的暂定正确。**
>
> 依据（〔串〕+〔码〕+〔析〕，按强到弱）：
> 1. 〔码〕计数器在托管层叫 **`deadline_resets`**，是 snapshot 的 **14 个必需字段之一**（`WinAirCast.decompiled.cs:5885`，`:24588` 读取做健康判定），缺了它 `ParseSnapshot` 直接抛 `JsonException`。schema 里**没有** `seq_skips` / `packets_dropped` 这类字段。
> 2. 〔析〕重锚日志块（`0x140021150`–`0x1400212b0`）只做三件事：算 `behind_ms = (now - xmm12)/xmm13*xmm14` 存 `[rbp+0x368]`、拼日志、由上游 `cl` 标志决定是否记；块内**看不到任何 seq/rtptime 字段的写**（但该块只是一条日志路径，整条重锚分支的完整 CFG 未逐边验证，故仍属强推断而非确证）。
> 3. 〔串〕紧邻的 `「… aborting; discarding <n> buffered packet(s)」` 说明丢弃发生在**本地发送队列**（还没分配 seq 的包），不是 seq 空洞。
>
> 三条推论：
> 1. 会跳 seq 的机制一定要单独计数（否则重传风暴无法归因）。没有这个计数器 ⇒ 不跳。
> 2. 文案 "deadline **one frame ahead**" 描述的是"把下一次发送的截止时刻挪到下一帧边界"，不是"推进时间轴槽位"。若是 (b)，文案会是 `skipping <n> packets`。
> 3. 紧邻的串 `「… aborting; discarding <n> buffered packet(s)」` 是**本地发送队列**的丢弃（还没分配 seq 的包），不是 seq 空洞。
>
> 逐条：
> 1. **seq 不跳，rtptime 也不跳**。抓包会看到两者都连续；变的是"同一 rtptime 被发出的墙钟时刻"。
> 2. **sync 包要跟着平移** —— 你们已经这么做了，正确。补一条上一轮的发现：WAC 的 deadline 是**单一权威源**，所以它只改一处；AF 的 `base_ns` 是 `send_packets` 的**局部变量**（`session.rs:1041`），必须逐个使用点加 `anchor_shift_ns()`，这正是你们踩过的坑。
> 3. **不需要静音包补齐**（seq 没有空洞，无从补起）。
>
> ⚠️ 一处可以简化：所谓"留一帧相位余量"不是必须的。WAC 就是把 deadline 直接放到**下一帧边界**（"one frame ahead"），不是"额外多留一帧"。两者等价，不必叠加。

### Q2. catch-up budget 的具体数值和升级条件？ `[部分确认]`

AirFlash 现有阈值：`due > 8` 包（352 帧 @44.1k ≈ **63.8 ms**）或 `now + 2ms >= earliest_render` 即判 `timeline_expired`。

**我们暂定**：`due ∈ [2,8]` 补淡出静音（不变）；`due > 8` 或贴 render 截止线 → 重锚；**10 秒内重锚 3 次**仍追不上 → 才 `timeline_expired`。

**需要确认**：

1. WAC 的 catch-up budget 是 60 ms 还是别的（日志里的 `<n> ms` 是运行时值，抓一次日志就知道）？
2. "重锚也有上限"这条规则 WAC 有没有？上限是次数/时间窗，还是"连续 N 个包都没追上"？
3. 重锚时 WAC 会不会触发一次 `SETRATEANCHORTIME` 或重新 SETUP？

> **答复**：**阈值数值无法静态确定，但语义和"有没有上限"可以确定。**
>
> **budget 的语义**（确定）：它来自 sender timing，周围就是 `「Sender timing telemetry: total_packets=<n> interval_packets=<n> elapsed_ms=<n> expected_ms=<n> avg_jitter_ms=<n> max_jitter_ms=<n>」`。也就是 budget 用 **"这段时间该发多少包 vs 实际发了多少包"折算成 ms** 来算，不是一个裸的毫秒常数。
>
> **因此不要用固定 60 ms**：WAC 的 budget 跟 `interval_packets` 的采样窗口联动，设备抖动时会自适应。建议取 **一个发送窗口的 1.5~2 倍**，并按 interval 长度动态算。
>
> **"重锚上限"：WAC 没有次数上限**（托管层确定）。snapshot schema 里只有 `deadline_resets` 这一个**累加**计数，没有任何窗口计数（`reanchor_*_window` / `attempts_in_10s` 之类在 C# 与字符串池里均 0 命中）。`[Status] REBUILDING` / `BACKEND_RECOVERY_FAILED` 属于**进程级**恢复，不是重锚级。
> → **WAC 的分层是：引擎无限重锚 + 只报数；上层 `CalibrationHealthSnapshot` 拿这个数判健康。**
> → **强烈建议照抄这个分层**：把"3 次/10 s 就 `timeline_expired`"从引擎挪到策略层。引擎只加 `deadline_resets` 计数，你的验收（`timeline_expired == 0`）也还在。
>
> **不触发 `SETRATEANCHORTIME`，也不重发 SETUP**（确定）：`SETRATEANCHORTIME` 在二进制里 **0 命中**；RTSP 方法表只有 `OPTIONS / ANNOUNCE / SETUP / RECORD / PAUSE / FLUSH / TEARDOWN / GET_PARAMETER / SET_PARAMETER / POST / GET / SETPEERS`。这条担心可以直接删掉。

### Q3. 重锚与"源侧断点"的关系？ `[已确认]`

WAC 会话状态机里有 `TrackBoundary` / `Reanchoring` 两个一等状态。AirFlash 没有"曲目边界"概念（永远是一条直播流）。

**我们暂定**：不引入 TrackBoundary；所有重锚都记 `timeline_reanchored` 计数 + 位移量。

**需要确认**：WAC 的 `Reanchoring` 状态是否只在 `TrackBoundary` 后出现？普通调度停顿用的也是这个状态吗（还是只有计数器）？

> **答复**：**是兄弟状态，不是父子；你们不引入 TrackBoundary 是对的，但建议把 Reanchoring 提为一等状态。**
>
> 两者在**同一个会话状态枚举**里连续排列（11 个变体）：
> `Disconnected Connecting Primed Playing Paused RetainBuffer PausedFlushed TrackBoundary Reanchoring Stopping Failed`
>
> - `TrackBoundary` = 曲目边界语义（要等 FLUSH / 新 `RTP-Info`）
> - `Reanchoring` = 时间轴重锚
>
> **托管层 `Reanchoring` 0 命中** —— 它没有映射到 UI，纯引擎内部。所以"普通调度停顿也用它"是安全推断：**一个一等状态，而不是只有计数器**。
>
> **建议**：计数之外，把重锚做成状态位（`SenderState::Reanchoring`）。理由：有状态 ⇒ 重锚期间可以**拒绝新的发送请求**，避免重锚瞬间与新包并发导致串序。你们现在只记计数，这一点是裸露的。

---

## 二、lateness 伺服与漂移（P0-3）

### Q4. servo 的输入量到底是什么？ `[已确认]`

报告给的模板是〔串〕`「PTP timeline servo sample: lateness=<a>ms filtered_error=<b>ms correction=<c>」`。

我们理解 `lateness = 实际发送时刻 − 计划发送时刻`（正数=晚）。但也可能是"实际发送时刻 − 该包应在对端播放的时刻"（含 lead），二者差一个常量 lead，对滤波器影响不大，但对"步进判定 250 ms"影响很大。

**我们暂定**：`lateness = now − schedule.deadline()`（本地节拍偏差，单位 ms，正=晚），一阶低通 `filtered_error += 0.2 × (sample − filtered_error)`。

**需要确认**：

1. `lateness` 是"vs 本地计划发送时刻"还是"vs 对端播放时刻"？
2. `correction` 的单位是 ms 还是 samples？正号表示"提前发"还是"推迟发"？
3. `correction` 作用在什么上：(a) 发送提前量 lead、(b) 重采样比、(c) 帧插删、还是 (d) sync 包映射？

> **答复**：**输入是"本地发送时刻"，不是差值；lateness 是 vs 本地节拍（你们的 (1) 猜对了）。correction 是 ppb，不是 ms。**
>
> 反汇编（调用方 `0x140026a60` → 伺服状态机 `0x140007e90`）：
> ```
> 0x140026b63  add   rsi, rbx          ; rsi = now_ns + [rdi+0x60]  (饱和加)
> 0x140026b86  mov   [rdi+0x80], r14   ; r14 = [rdi+0x68] - rsi   → 存"余量"
> 0x140026b95  mov   rdx, rsi          ; 传给伺服的是这个"发送时刻"
> 0x140026b98  call  0x140007e90
> ; 伺服内部
> 0x140007eca  sub   r10, r8           ; delta = sample - baseline
> ```
> 表达式里**只有 now + 本地偏移**，没有任何网络/渲染项 ⇒ **与对端播放时刻无关**，lead 也不进这个环（lead 已经体现在 deadline 里）。
>
> **baseline 不是第一个样本**：预热期把样本存进 Vec，**排序后取中位数**（`0x1400080b7 shr rdi,1` 取中位）。`≥4` 个才出基线，`≥20` 时走全排序。若用"第一个样本当 baseline"，开局会有一段假 engage。
>
> **`correction` 单位 = ppb（串里就写着 `correction=<c>」ppb`）**，不是 ms。
> **`correction_ppb = filtered_ns / 20`**（`filtered_ns * 1e9 / 2e10`，即 **20 秒时间常数**），夹在 **±50000 ppb = ±50 ppm**。
> 也就是：**1 ms 稳态误差 → 50 ppm 修正**。
>
> **作用对象 = (b) 重采样比**。而且量程惊人地吻合：AF 的 `live.rs:545-547` 那个 `clamp(-0.0005, 0.0005)` 正好是 **±500 ppm**，比 WAC 的 ±50 ppm 宽一个数量级 —— 你们现有的环路**量程够用，缺的是增益与死区**（见 Q5）。
>
> **正号 = 让媒体时钟走快**（提前发/少等）。

### Q5. 伺服常数：α、step 阈值、engage/release 门槛？ `[已确认]`

报告从常数池推断：`0.2`（滤波系数）、`250 ms`（step 判定）、`5 ms`（最小修正粒度/engage 阈值），并明确标注这是**推断**。

**我们暂定**：`ALPHA=0.2`、`STEP_MS=250`（|sample| 超此值判断点、重置基线）、`ENGAGE_MS=5`、`RELEASE_MS=2`、修正速率上限 **1 ms/包**、`correction` 夹在 `[-40, +40] ms`。

**需要确认**：

1. 这些常数在 WAC 里能否确认（或从行为上观察：比如 5 ms 是不是"每次同步最多步进 5 ms"的 slew，而不是 engage 阈值）？
2. `correction` 的物理上限是多少？超出后 WAC 是重锚还是拆会话？

> **答复**：**四个常数全部字节级确认，但其中三个的含义与你们假设不同。**
>
> | 常数 | 地址 | 值 | 真实语义 |
> |---|---|---|---|
> | α | `0x1406c9818` | **0.2** | `filtered += 0.2*(sample-filtered)` ✅ |
> | 断点阈值 | `0x1406c9810` | **250000000.0 ns = 250 ms** | `|delta| > 250 ms` → **整机重置**（不是"步进判定"） |
> | engage | `0x1406c9820` | **5000000.0 ns = 5 ms** | `|filtered| > 5 ms` 才 engage（**不是"每次最多步进 5 ms"**） |
> | release | `0x1406c9828` | **1000000.0 ns = 1 ms** | `|filtered| ≤ 1 ms` 才 disengage（你们写的 2 ms 不对） |
> | 增益 | `0x1406c9830`/`0x1406c9838` | **1e9 / 2e10** | `correction_ppb = filtered_ns/20`（20 s 时域） |
> | 输出夹 | 立即数 | **±50000** | `correction ∈ [-50000,+50000] ppb = ±50 ppm` |
>
> **死区是非对称的**：enter 5 ms / exit 1 ms（迟滞），不是同一个阈值。
>
> **250 ms 分支做什么**（容易误解）：`|delta| > 250 ms` 时走 `0x140007fce → 0x14000814e`，那里把 `[rcx]=0`（退回未初始化）、`[rcx+0x20..0x30]` 清零（**连带 filtered 归零**）、样本表清空，然后**递归重入**。所以是 **servo 完全重置 + 重新预热**，不是"保留状态只换基线"。
>
> **超出 correction 上限会怎样**：不重锚、不拆会话，**直接削顶**（两条 `cmovnl/cmovl` clamp）。所以"修正饱和"在 WAC 里是**静默的**，只体现在遥测的 `correction` 值上。
>
> ⚠️ 你们把 250 ms 当"步进判定"会做错：它其实是**断点检测器**，工作在**未滤波的原始 delta** 上（`0x140007ee1 andpd` 取绝对值后直接比），与 filtered 无关。

### Q6. `interval_correction=<n> frame(s)` 的量级与位置？ `[部分确认]`

报告写"每隔若干帧插/删一帧（22.7 µs），不改音高"。

**我们暂定**：在 PCM 域、编码之前，按累加器每攒够 1 帧（352 samples）的时间就插一帧或删一帧；插删点复用 `Splice::boundary()` 做过渡。

**需要确认**：

1. `<n>` 是"每 n 帧插删一次"（间隔），还是"本次插删 n 帧"（数量）？
2. 插删发生在编码前（PCM）还是编码后（ALAC 帧）？
3. 插删是否只对 PTP PCM 生效（WAC 的日志里写的是 `PTP PCM drift control`）？ALAC 路径走什么？

> **答复**：**`<n>` 是"间隔"（每 n 帧插删一帧）；位置在 PCM 域、编码前。**
>
> 依据：串名就是 `interval_correction`，且同一条日志里另有独立的"帧数/样本数"字段。WAC 的记录点是 `「PTP **PCM** drift control: interval_correction=<n> frame(s)」`（`0x1408e0958`），带 4 个参数：`interval_correction`、`+/-` 方向、总样本数、观测帧数。
>
> **量级自检**（很有用）：44.1 kHz / 352 帧 = **125.2 帧/秒**。
> - 50 ppm → 每 **160 秒**插删一帧
> - 5 ppm → 每 **1600 秒**插删一帧
>
> 所以**正常会话里它几乎不动**，只在长会话累积。你们按"每攒够 1 帧时间就插删"实现时，务必确认这是"累计量跨阈值才动一次"，否则会频繁动。
>
> **编码前**（PCM）确定：串名带 `PCM`；ALAC 是 352 样本帧，帧内插删会破坏 `ALACSpecificConfig.frameLength` 的定长语义，所以 ALAC 路径**没有**这条。
>
> **ALAC 路径怎么办**：用**重采样比**（`SincFixedIn` 的 ratio）。这也解释了为什么伺服最终的输出量是 **ppm** 而不是帧数 —— 两条路径共用一个 ppm 量，PCM 路径再把它翻译成帧间隔。

### Q7. 漂移合理性上限？ `[部分确认]`

〔串〕`「PTP drift estimate <n> ppb rejected as implausible (<reason>)」`。

**我们暂定**：`|ppb| > 1000 ppm`（=1e6 ppb）判无效，保留上一次估计；`reason` 用 `implausible`。

**需要确认**：WAC 的上限是多少（100 ppm？1000 ppm？），以及 `<reason>` 有哪些取值。

> **答复**：**阈值数值无法静态确定，但可以给出严格上界和推荐值。**
>
> `<reason>` 与阈值都是运行时拼的，比较指令没定位到。不过有两条硬约束可以夹出范围：
> 1. 阈值必须 **> 50 ppm**（否则正常伺服的输出会被误判）；
> 2. 阈值必须 **< 500 ppm**（AF/WAC 的重采样器量程就到这里，再大根本修不了还照发没有意义）。
> 而且相邻串 `「PTP drift correction off: no fresh PTP timing for <n> (samples=<n>)」` 说明**样本新鲜度**也是一道独立门。
>
> **推荐：`|ppb| > 200_000`（200 ppm）判 implausible。** 理由：200 ppm ≈ 20 °C 温漂量级，超过它就不是漂移而是测量坏了。
>
> **你们的 1000 ppm 太松**：1000 ppm 在 44.1 kHz 上是 **44 样本/秒**，那是时钟坏掉，不是漂移。
>
> **`<reason>` 可枚举**（从相邻串归纳）：`no fresh timing` / `few samples` / `out of range`。
>
> 若想实测：喂一个已知 ppm 偏差的合成时钟，二分找到拒绝点 —— 这是唯一能定死数值的办法。

---

## 三、自适应 lead（P0-2）

### Q8. 运行中改 playout lead 的机制？ `[已否决]`

WAC 有 `PROFILE_LATENCY <ms|auto>` / `CALIBRATION_LATENCY <ms|auto>` / `DEV_PTP_LATENCY`，说明 lead 可以**运行中**改（不必重连）。

**我们暂定**：引擎新增 `set_lead { ms, slew_ms }`；同时平移"本地发送 deadline"和"sync 包的 networkTime"，每次同步步进 ≤5 ms（slew），保证 seq↔rtptime↔网络时间仍线性。

**需要确认**：

1. WAC 改 lead 是改 sync 包映射（"Locked PTP lead gliding on the wire"）还是重新 SETUP 改 `latencyMin`？
2. 会不会触发对端重缓冲/咔哒？WAC 的 slew 步长是多少（日志里的 `slew=5` 是不是"每次同步最多 5 ms"）？
3. `latencyMax` 在 lead 变化时要不要跟着改（WAC 常态是 `latencyMin + 100 ms`）？

> **答复**：⚠️ **这一条你们的做法要改：不要平移 sync 包的 networkTime。**
>
> WAC 是**两套东西，分开管**：
>
> | 层 | 证据串 | 行为 |
> |---|---|---|
> | **协议窗口**（`latencyMin/Max`） | `「Receiver playout lead overridden to <n> samples); **advertised latencyMin remains <m> samples**; rtp_origin=…」` | **不改** |
> | **播放 lead（运行中）** | `「Live PTP playout lead adjusted immediately to <n> samples); **protocol window remains <m> samples (no RTP origin reset)**」` | 立即改本地映射 |
> | **自适应叠加** | `「Adaptive PTP playout lead slew: applied <n>ms (target=<n>ms, RTT=<n>ms, rtt_median=<n>ms, interval=<n>, media=<n>, grace=<n>, streak=<n>, baseline=<n>)」`（`session/latency.rs:1395`） | 按 RTT/streak 爬 |
> | **锁定态** | `「Locked PTP lead gliding on the wire: <n>」` | 锁定后缓慢滑 |
> | **配置态** | `「PTP slew target configured: target=<n> samples), step=<n>」` | target + step |
>
> 逐条：
> 1. **改本地 playout lead**。不重发 SETUP、不改 `latencyMin`、**不重置 RTP 原点**（原文 "no RTP origin reset"）。
> 2. **slew 步长是"configured step"，不是常数 5**。`slew=5` 是遥测里的一次应用值，别写死。对端**不会重缓冲**，因为协议窗口没变。
> 3. **`latencyMax` 不跟着改**。原文只说 `advertised latencyMin remains`；既然 min 不变，`latencyMin + 100 ms` 的关系也保持不变。
>
> ⚠️ 关键差别：你们现在"同时平移本地 deadline 和 sync 的 networkTime"，**对端会看到映射跳变**——这正是 WAC 特意避免的。正确做法：**只改本地丢弃/发送的提前量，sync 包的 `rtp→network time` 映射保持不动**。
>
> 这也顺带解释了为什么 AF 不能直接改 `base_ns`：一改就是"映射跳变"。

### Q9. 候选阶梯与 floor 的实际生效方式？ `[已确认]`

报告给的阶梯（HomePod solo 50/40/30、floor 50；HomePod group 120/110/100/90；含原生立体声对 ≥120 等）来自 `LatencyCalibrationPolicy` 的**候选列表**，但报告同时说 `VerifyRules`/`GradeBProbePlanner` 是死代码，生产线用的是 `IsHealthy` + `ComputeRecommendation`。

**我们暂定**：把候选阶梯直接写成表，用 8 s 遥测窗口做归因（`SourceGlitch` 不升档），升降档用 slew 滑过去。

**需要确认**：

1. 生产线的 `ComputeRecommendation` 到底按什么规则升/降档（步长 10 ms？直接跳到下一候选？）
2. `SoloHomePodAlacValidatedFloorMs=50` 是"低于 50 就直接拒绝"还是"低于 50 仍按用户要求应用、只打日志"（日志写的是 `applying as requested`）？
3. 声学标定（−250…+250 ms）报告说"当前停用"，我们是否也先不做？

> **答复**：**WAC 没有"阶梯"，`ComputeRecommendation` 一次算出三个方案。`floor` 不拦用户设定，只打警告。**
>
> 完整公式（`WinAirCast.decompiled.cs:38704`，可逐行复现）：
> ```
> Rtx = ComputeRRtx(d, n, wifi)                 // gap-derived P95 优先，溢出置 OverflowFlag
> L   = d.Lateness.P99_9 ?? d.Lateness.P99 ?? 0.5
> SigmaTerm = max( (grade==A ? (RttDerived?3.5:3.0) : grade==B ? 4.0 : 4.0) * n.Sigma
>                  + tdev.Width/2,
>                  grade>=CPlus ? 15.0 : 0.0 )
> floor = max( max(floorClass, tdev.Low + 16.0),
>              max(n.DPath + n.JNet + Rtx, recvClampMs) )
> raw   = max(floor, tdev.Value + n.DPath + n.JNet + Rtx + L + SigmaTerm)
> conservative = Ceil5(floor)
> balanced     = max(Ceil5(floor), Round5(raw - SigmaTerm/2))
> stable       = Round5(raw + max(40.0, 2*SigmaTerm))
> ```
>
> 逐条：
> 1. **没有步长、没有升降档**。三个方案一次算完。`IsHealthy`（`:38604`）只判健康，**决策策略要你自己写**——WAC 没给，所以你们那条"8 s 窗口归因"是净增能力，不必对齐。
> 2. **`SoloHomePodAlacValidatedFloorMs=50` 是 `floorClass` 的一个取值**（进 `max(...)`），**不拒绝用户设定**。低于 floor 时**照样应用**，只打：`「Below this mode's validated {0} ms floor; tearing or dropouts are possible. **Applied exactly as you measured it.**」`（`:5875`）。
>    → **你们"低于 50 直接拒绝"要改成"应用 + 警告"**。
> 3. **声学标定可以不做**：`ACOUSTIC_OFFSET` 命令在、`SetAcousticOffset` 在，但**零调用点**（UI 未接）。同意先不做。

---

## 四、元数据与反向控制（P0-4 / P3-1）

### Q10. `SET_PARAMETER` 的三种格式，HomePod 认哪个？ `[已确认]`

报告列了：`progress: start/curr/end`（text/parameters）、`application/x-dmap-tagged`（DMAP TLV）、`image/jpeg`（封面）。

**我们暂定**：三种都发（先 progress，再 DMAP 曲目信息，再封面），节流：进度变化 ≥1.5 s 或状态变化时发。

**需要确认**：

1. HomePod（AirPlay 2 + PTP）实际接受哪种？text/parameters 的 `progress` 单位是毫秒还是采样？
2. 曲目信息是 DMAP 更稳还是 text/parameters 的 `title:/artist:/album:` 更稳？HomePod 对两者反应如何？
3. 封面是否必须 `image/jpeg`、≤2048 px？空 body 能不能清封面？
4. 这些 `SET_PARAMETER` 是发在 **control 连接**（RTSP）还是 **event 连接**上？AirFlash 目前只有 control 连接可用。

> **答复**：**全部走 control 连接（RTSP）；`text/parameters` 的用途只有 progress 和 volume，没有 title/artist。**
>
> 字节级依据：
> - `【progress: 】` + `「Preparing Now Playing metadata」` + `「Now Playing progress not sent」` + `「Now Playing progress response」` + `「Now Playing progress request failed」`
> - `「GET_PARAMETER volume.」` + `「receiver current volume=」` + `【volume: 】`（`airplay-rtsp/src/session.rs:484`）
> - `Content-Type: text/parameters | application/x-dmap-tagged | application/sdp`
>
> 逐条：
> 1. **progress 单位是毫秒**，体是 `progress: <start>/<curr>/<end>`，`text/parameters`。HomePod 认。
> 2. **曲目信息用 `application/x-dmap-tagged`（DMAP TLV）**。`text/parameters` 里的 `title:/artist:/album:` **在 WAC 里零证据** —— 别在这上面花时间。
> 3. **封面固定 `image/jpeg`**（C# 里 `_cachedArtworkMime = "image/jpeg"` 三处、`PrepareArtwork` 返回值两处），有 **≤4 MiB** 上限与 `QualityLevel=95`。**清封面不要发空 jpeg**：WAC 用 `clearNowPlaying` 路径，不是空图。
> 4. **只在 control 连接发**。Events 连接是纯 plist（`events_channel.rs` 只处理那 7 个键），不做 SET_PARAMETER。→ 你们"只有 control 连接可用"**不构成限制**。
>
> ⚠️ 节流：WAC 的进度是 **2 秒定时器**（`_progressTimer = new Timer(..., TimeSpan.FromSeconds(2.0), …)`，`:35102`），**不是** Δ≥1500 ms。你们的 1500 ms 门限可以留（更省），但要知道 WAC 是定发 + 状态变化即刻发。

### Q11. 事件通道的 plist 键名与取值？ `[已确认]`

**我们暂定**：按 `mrCommand` → `command` → `cmd` 的优先级取命令；`action == "volume"` 时取 `value: real`。

**需要确认**：

1. `mrCommand` 的真实取值是字符串（`"play"`/`"pause"`）还是位掩码数字？WAC 的映射表在哪？
2. 音量事件的 `value` 是 0..1、-144..0 dB，还是 0..100？
3. 是否存在 `type`/`options` 字段需要一起解析（报告提到 `options`/`arguments`）？

> **答复**：**7 个键名精确、`mrCommand` 是整数、body 是单个 dict、音量不走这里。**
>
> - 键名（`events_channel.rs:67/105/106` 连续排列）：**`value` `command` `mrCommand` `cmd` `action` `options` `arguments`** —— 你的优先级顺序正确。
> - **`mrCommand` 是整数**（MRP 枚举），不是字符串。**WAC 里没有可见的映射表** —— 它把整数**透传**给托管层，由 C# 解释。
>   → 所以你的映射表（0 Play、1 Pause…）与公开约定一致，但**必须容忍未知值**（忽略 + 计数）。别猜。
> - **body 是单个 dict**（键平铺，不是数组）。
> - **音量不走 Events**：走**独立隧道** `「MediaRemote volume tunnel to '<ip>'」` / `「MediaRemote volume from '<ip>'」` / `「MediaRemote volume <n> for output <n>」`。→ AF 现在"只发 SET_PARAMETER、不处理事件音量"是**对的**，WAC 也不从 plist 取音量。

### Q12. SMTC 侧的去抖与进度策略？ `[已否决]`

报告：2 s 轮询 + 300 ms 去抖 + 进度策略（Δ≥1500 ms / rate 变化 / duration 变化）。

**我们暂定**：C# 侧 `NowPlayingService` 用 2 s 轮询 + 300 ms 去抖；进度变化 ≥1500 ms 或播放状态变化才下发。

**需要确认**：300 ms 去抖是"事件触发后等待 300 ms 再读"还是"两次相同事件间隔 300 ms 才算稳定"？封面转码是"超过 4 MiB 或边长 >2048 才转"还是"一律压到 2048"？

> **答复**：⚠️ **轮询确实是 2 s，但"300 ms 去抖"在 WAC 里不存在 —— 请删掉。**
>
> - **2 s**：`_progressTimer = new Timer(delegate {…}, null, TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.0));`（`WinAirCast.decompiled.cs:35102`）。✅
> - **300 ms**：C# 里没有对应常量（`300` 只出现在别处）。这条是报告从外部推的，**请改成 2 s 或直接去掉**。
> - **封面策略**：**不是"超 4 MiB 才转"，而是"一律重编码成 jpeg，再按 4 MiB 封顶"**。
>   - 输出固定 `image/jpeg`（`:35809` / `:35834`）
>   - 上限 **4194304 B**（`:35805`；超了 `return null`）
>   - 质量 **95**（`:35801`）
> - **额外可抄的一条**：音量下发节流 **30 ms** —— `VolumeCommandInterval = TimeSpan.FromMilliseconds(30.0)`（`:19657`），按 (ip, key) 建 per-target timer。

---

## 五、采集侧（P1-7）

### Q13. "这一包是否延续上一包"的判据？ `[部分确认]`

〔串〕`「WASAPI discontinuity flag on a packet that continues the previous one; ignored (occurrence <n>)」`。

**我们暂定**：记录上一包的 `IAudioCaptureClient::GetBuffer` 的 `pu64DevicePosition`（或 `pu64QPCPosition`）+ 帧数；若本包 `position == last_position + last_frames`（允许 0 误差）且 `qpc` 与预期偏差 < 1 个设备周期，则判"延续"，忽略 `DATA_DISCONTINUITY` 并计数。

**需要确认**：

1. WAC 用的是 device position 还是 qpc 连续性？
2. 容忍窗口多大（严格相等？±1 帧？±1 个 engine period？）
3. 判为"真断点"后，WAC 是清队列 + `reset()`，还是只清队列不 reset 重采样器？

> **答复**：**判据是"连续性判断"（确定），具体用哪个信号无法静态确定；但你们要补的不是判据，是"重开端点"。**
>
> 两条精确串：
> - `「[WASAPI discontinuity flag on a packet that **continues the previous one**; ignored (occurrence <n>)」` —— `capture.rs:3053`
> - `「WASAPI data discontinuity reported by capture client (occurrence <n>)」` —— `capture.rs:3036`
>
> 文案 "continues the previous one" 证明它做的是**连续性判断**，不是"有 flag 就信"。但 `pu64DevicePosition` / `QPC` 是结构体字段名，不是字符串，搜不到；Rust 又无源码。
>
> **建议用 device position**：这是唯一能表达"延续"的量（QPC 只能表达"时间对得上"，而 process-loopback 的 QPC 有额外延迟，会误判）。
> **容忍窗口用严格相等 + 帧数**：任何 ±1 帧容忍都会把真正的一帧断裂漏掉。
>
> **真断点后的动作（这条你们漏了）**：
> `「… discarding <n> buffered packet(s)」` + `「produced no audible samples for <n>ms after a data discontinuity; **reopening endpoint**」`
> → WAC 是**重开端点**，不只是清队列 + reset。见 `report/03-音频流水线.md §3.5.3`。

### Q14. dither 的实现与静音处理？ `[已确认]`

WAC 日志：`「… -> <rate> Hz/16-bit dithered, <x> ms filter」`。

**我们暂定**：TPDF dither（±0.5 LSB）+ 一阶误差反馈整形；**全零帧不加 dither**（否则静音段会多出 −90 dBFS 噪声，且会破坏 AirFlash 的"静音包=精确 0"语义）。

**需要确认**：

1. WAC 的 dither 是 TPDF 还是矩形？位深是恒定 16 bit 吗？
2. 全零帧（静音）也会抖吗？WAC 的"空闲 keepalive 直接静音"是精确 0 还是带抖动？
3. `<x> ms filter` 是抗混叠滤波器的长度吗（比如 2 ms）？

> **答复**：**dither 存在，但不在"该抄"的位置 —— 它是 WASAPI 原生 16 bit 路径的副产物。**
>
> 两条格式串（分别对应两条路径）：
> - `「WASAPI capture started: <dev> (native <n>Hz/**16-bit dithered**, <n> ms filter)」` ← 原生路径，WAC 自己重采样并抖动到 16 bit
> - `「WASAPI capture started: <n>bit int/<n>ch, **Windows converts**; -> <n>」` ← 回退路径，交给 WASAPI
>
> 逐条：
> 1. **算法无法确定**（无 TPDF / rectangular / noise-shaping 的串）。**位深恒定 16 bit**（两条串都写死）。
> 2. **`<n> ms filter` 是重采样滤波器长度（ms），不是 dither 参数** —— 别把这两件事绑一起。
> 3. **"全零帧是否抖动"无证据**。WAC 的 keepalive 是"直接静音"。
>
> **结论：不要照抄 WAC 的 dither。** 它是 16 bit 固定输出的副产物，不是独立设计。AF 的 TPDF + 误差反馈若已工作就保留；**你们"全零帧不加抖"的理由（遮盖逻辑依赖全零判定）是 AF 自己的约束，WAC 没有这个约束所以无可对比** —— 这条可以标记为"AF 自主决策，无需对齐"。

### Q15. 进程级捕获必须自转格式吗？ `[已确认]`

WAC 的进程回环走 `INCLUDE_TARGET_PROCESS_TREE`，AirFlash 现在是 `AUTOCONVERTPCM` + 显式 S16 格式。

**我们暂定**：改为"先取 mix format 自转，失败再回落到 AUTOCONVERTPCM"。

**需要确认**：WAC 的进程回环是否真的用 `AUTOCONVERTPCM`？它怎么处理"进程回环的 mix format 是浮点 48 kHz"这种情况？

> **答复**：✅ **确认：WAC 就是"先原生、失败才让 Windows 转"，与你们的暂定完全一致。**
>
> 〔串〕`「WASAPI native capture (<fmt>) refused: <err>; using <n>Hz/16-bit with Windows conversion」`
> 另有一个环境变量 **`WINAIRCAST_CAPTURE_NATIVE`** 可强制走原生。
>
> 进程回环的 mix format 是浮点 48 kHz ⇒ 走的就是 "refused → 回退"，日志落到第二条格式串（`bit int/<n>ch, Windows converts`）。
>
> **建议**：照抄这个"探一次 → 失败回退 → **把失败原因打出来**"的结构。AF 现在无条件 `AUTOCONVERTPCM`，等于**主动放弃了原生路径的更低延迟**，而且问题发生时没有任何诊断线索。

---

## 六、控制面与时钟（P1-9 / P1-6）

### Q16. control EOF 之后的重建策略？ `[已确认]`

〔串〕`「Control EOF policy note: … single Apple TV reconnects the member, multi-device preserves shared RTP unless media fails」`。

**我们暂定**：feedback/event 连续失败时只记遥测并尝试重建（有限次），RTP 仍在发包时**不**拆会话；只有媒体面持续失败（12 s 无成功发送）或确认源静音才拆。

**需要确认**：

1. 单 Apple TV 场景"reconnects the member"是重建 RTSP（TEARDOWN+SETUP）还是只重连 feedback？重建期间 RTP 停不停？
2. 多设备场景 control 永久丢失后，WAC 什么时候才拆？（源静音？用户停止？）
3. `clock_lost`（PTP 失步）WAC 是"用 last-known offset 继续"多久才拆？

> **答复**：**原文一字不差，且给了前提：加密 RTSP 中途不能换密钥。**
>
> 〔串〕`「Control EOF policy note: encrypted RTSP cannot be re-keyed mid-session without re-pairing; single Apple TV reconnects the member, multi-device preserves shared RTP unless media fails」`（`connection.rs:485`）
> 紧邻 `:473`：`「Encrypted RTSP control early EOF (first occurrence): blocks_read=<n>, partial_plaintext=<n>B, detail=…」`
>
> 逐条：
> 1. 单 Apple TV = **重建该 member 的 RTSP**。但前提是"加密会话不能中途换密钥"，所以重建 = **重新握手/配对**。该 member 的 RTP 在重建期间**停**（RTP 属于它自己）。
> 2. 多设备 = **保留共享 RTP**，除非媒体面失败。✅ 与你们暂定一致。
> 3. `clock_lost`：**没有"用 last-known offset 继续 N 秒"的机制**。有的是 `「PTP drift correction off: no fresh PTP timing for <n> (samples=<n>)」` —— 即**冻结漂移修正**（不再更新 correction，继续用最后的值），**不拆会话**。
>    → 所以答案是：**off = 冻结 correction，不是计时拆会话**。你们要加"多久才拆"的话，那是 AF 自己的策略。

### Q17. BMCA 的比较键与报文形式？ `[部分确认]`

**我们暂定**：用 `(priority1, clockClass, priority2, clockIdentity)` 字典序比较；只处理**单播** Announce（AirPlay 的 PTP 是单播 profile）；超时 3 个 Announce 周期（3 s）回 Listening。

**需要确认**：

1. WAC 的 Announce 是单播还是组播（224.0.1.129 / 802.1AS 的 01:80:C2:00:00:0E）？
2. 比较键是否包含 `stepsRemoved` 和 `grandmasterIdentity`？`priority2` 用到了吗？
3. Apple TV 家庭影院组里，WAC 真的会 yield 吗？yield 之后它是否还发 Announce（作为 slave）？

> **答复**：**单播；比较键不要包含 `priority2`（WAC 里就没有）。**
>
> - **单播**：AirPlay 2 的 PTP 是 unicast profile。`224.0.1.129` / `01:80:C2:00:00:0E` 在二进制里 **0 命中**。
> - **比较键可用**：`priority1` / `clockClass` / `stepsRemoved` / `gid` / `grandmaster` / `BMCA complete` 都有串。
> - **`priority2` 0 命中** → 用它你没数据来源，**删掉**。
> - **`masterclock` 0 命中** → 它不用 PTP 的 Master/Slave 术语，而用 `grandmaster`。
>
> 建议的实现：`(priority1, clockClass, stepsRemoved)` 字典序，相等时用 `gid` 打破平局；**3 个 Announce 周期超时回 Listening** 合理。
>
> **yield 后是否还发 Announce**：见 **Q27**。WAC 会让位（`「BMCA: Yielded master to remote, transitioning to slave mode」` → `「BMCA: Entering slave loop, syncing to <ip> (filtering other peers)」`），但**只在 SETUP 协商期**；进入 slave loop 后只发 `Delay_Req`、收 `Sync/Follow_Up`，**不再发 Announce**（与你的暂定一致）。

### Q18. 三件与抓包相关的小事 `[已确认]`

1. WAC 的 `RTP-Info` / `FLUSH` 双版本语义（`as in 2.1.5.0`）——有没有设备真的需要旧语义？AirFlash 需要保留哪些兼容分支？
2. `[Status] PREEMPTED` 的触发信号是什么（403？`Connection: close`？设备重新 announce？）
3. `AirPlay 2 预判`用 `features bit40 && srcvers >= 354.54.6`，而 AirFlash 用 `features bit41` 判 PTP。这两个 bit 的准确语义是什么？是否应该同时用？

> **答复**：三条都有明确结论，其中第 3 条会改变你们的实现。
>
> **1. `RTP-Info` / `FLUSH` 双版本** —— 存在，但只在**诊断日志**里：
> `「Elapsed PTP start (developer): first packet <n> ms after its RTP-Info was announced; its RTP time moves <n> packet(s) (<n> samples) ahead, **as in 2.1.5.0**」`
> → 有"旧固件行为"分支，但**只出现在 developer 诊断路径**。**建议：不保留兼容分支**，按现代语义发；需要时再加开关。
>
> **2. `PREEMPTED` 的触发信号** —— 无法确定单一信号。它和 **`takeover`**、**`limit`** 两个词紧邻出现（`events.rs:215` 附近）。
> → 大概率是**对端 TEARDOWN 时带的 reason，或 RTSP 403**。
> → **建议：不要把抢占绑死在一个状态码上**，用"未预期 TEARDOWN + 随后的重新 announce"这条组合特征判定。
>
> **3. bit40 vs bit41 —— WAC 只用 bit40，你们的 bit41 是错的。**
> ```csharp
> // WinAirCast.decompiled.cs:14525
> internal static bool SupportsAirPlay2(IReadOnlyDictionary<string,string> txt, bool isAirPlay)
> {
>     if (!isAirPlay) return false;
>     if ((ParseFeatureFlags(PreferNonEmpty(GetTxt(txt,"features"), GetTxt(txt,"ft"))) & 0x10000000000L) != 0
>         && Version.TryParse(GetTxt(txt,"srcvers"), out var v))
>         return v >= new Version(354, 54, 6);
>     return false;
> }
> ```
> - **bit40（`0x10000000000`）= "支持 AirPlay 2（含 buffered audio）"**，用于**列表预判**；`features` 缺失时读 `ft`。
> - **`0x20000000000`（bit41）在托管层 0 命中**（`1L<<41` / `1UL<<41` 也 0）。
> - **PTP 与否不由 feature bit 决定**，由 `CreateTopologyPlan` 按**设备型号 + 拓扑**决定（Apple TV solo → NTP；HomePod solo → PTP；其它 AP2 → PTP；立体声对 ≥120 ms）。
> - → **AF 应改用"型号 + 拓扑"表，而不是 bit41**。这就是 `report/15` 那张表的直接依据。

---

## 七、优先级与取舍（需要你拍板）

### Q19. 哪些项**不做**？ `[已确认]`

报告 [11-不该照抄](report/11-不该照抄.md) 已经排除了一批。剩下这些我们倾向"先不做"，请确认：

| 项 | 我们不做的理由 |
|---|---|
| 经典 RAOP / 初代 AirPlay（P3-5） | 工作量≈现有 AirPlay 2 路径的一半，且需要 FairPlay；引擎侧零基础 |
| `bufferStream` 缓冲流（P3-6） | 报告自己说"收益/成本比偏低，建议暂缓" |
| 声学标定向导（P1-10） | 依赖真实设备测量；先做"遥测 + 归因 + 自适应"，标定后置 |
| 云诊断上报（P2-14 的一半） | 开源项目不做后台上报；只做本地 `diag_summary` 导出 |
| AAC-ELD / OPUS（P3-9） | 只有"延迟压进 40 ms"才有意义，而 40 ms 梯队要 lead 全套配合 |

**需要确认**：这份"不做清单"是否同意？如果不同意，哪一项要提高优先级？

> **答复**：**同意 4 条，第 5 条（AAC-ELD/OPUS）建议保留在"暂缓"但改掉唯一理由。**
>
> - ✅ 经典 RAOP / 初代 AirPlay —— 同意不做（FairPlay 门槛 + 引擎零基础）。
> - ✅ `bufferStream` —— 同意暂缓。
> - ✅ 声学标定 —— 同意不做（WAC 自己也是死代码：`ACOUSTIC_OFFSET` 零调用点）。
> - ✅ 云诊断上报 —— **强烈同意**。WAC 也只写本地 `%APPDATA%\WinAirCast\backend.log`（`BackendLogPath`，`:30078`；解析在 `:30490`），没有任何上传。
> - ⚠️ AAC-ELD / OPUS —— 别只写"40 ms 才需要"。它还有**带宽**价值（ALAC 无损在 Wi-Fi 拥塞时是劣势）。但**顺序结论仍然成立**：先把 lead/servo 做对，再谈 ELD。所以放进"暂缓"没问题，**只是理由要改成"带宽是加分项，但排在 servo 之后"**。

### Q20. 验收口径 `[已确认]`

报告建议每次会话结束输出一行 `session_id, topology, codec, timing, applied_lead_ms, packets, rtx_req, rtx_resent, underruns, gap_filled, reanchored, servo_engagements, mean_lateness_ms, p99_lateness_ms, duration_s`。

**我们暂定**：AirFlash 已有 `transport_metrics`（1 s 一次）和会话结束的 `metrics` 事件，把上面这些字段加进去，并在 C# 侧写进日志。

**需要确认**：这条"会话行"是写到引擎 stdout（IPC 事件）、Windows 事件日志，还是 C# 日志文件？WAC 是写到哪个诊断桶里？

> **答复**：**WAC 落点是"本地日志文件 + IPC 事件"，不是 Windows 事件日志；而且它的 snapshot 字段集可以直接拿来对齐命名。**
>
> - 文件：`%APPDATA%\WinAirCast\backend.log`（`BackendLogPath`，`:30078`；多路径解析 `:30490`）
> - IPC：`SNAPSHOT` / `SNAPSHOT_UNAVAILABLE` / `[Status]` 等 stdout 事件
> - **无云上报**
>
> **WAC 的 snapshot 必需字段是这 14 个**（`ParseSnapshot`，`:5883`，缺一个就抛 `JsonException`）：
> `members` `queue_depth` `queue_capacity` `packets` `rtx_requested` `rtx_resent` `underruns` `input_drops` `decode_drops` `deadline_resets` `send_errors` `capture_glitches` `events_dropped` `path_delay_count`
> 外加直方图：`lateness_hist` `path_delay_hist` `rtx_gap_hist`（校验 `Histogram.Total == path_delay_count`，不一致就置 `InvalidData`）。
>
> **建议**：你们的会话行**拆成两层**：
> 1. 引擎每秒的 `transport_metrics` —— **用上面这 14 个名字**，这样以后能直接拿 WAC 日志做基准回归；
> 2. 会话结束行 —— 再拼 `servo_engagements / mean_lateness_ms / p99_lateness_ms / reanchored`。
>
> 命名对齐的收益远大于多写几个字段。

---

## 八、第二轮落地时新增的疑问

> 下面这些是在 P0-3 / P1-7 / P1-9 / P0-2 / P3-1 实现过程中新产生的；都已按「暂定做法」落地，改起来成本不高，但最好一次对齐。

### Q21. 有没有「运行中改提前量」的原生机制？ `[已否决]`

我们新增了 IPC `set_lead { ms }`：把请求值作用到 `MediaSchedule.shift`，同步锚点一起移动，**不重建会话**；人工设定的值不受 `relax()` 每秒回收一帧的影响。上限 2000 ms。

**需要确认**：

1. WAC 调整 lead 时是改 sync 映射、改 deadline、还是重发 `SETRATEANCHORTIME`？
2. 运行中改 lead，接收端会不会出现「同一段音频被播两次」或「跳一段」的可听后果？WAC 是否只在会话边界改？
3. 2000 ms 的上限是否合理，还是应当按接收端 buffer 能力动态取值？

> **答复**：⚠️ **同 Q8：改本地 lead，绝对不要改 `base_ns`。**
>
> 1. **改本地 playout lead**。不重发 `SETRATEANCHORTIME`（该命令 0 命中）、不改协议窗口、不重置 RTP 原点。
> 2. **不会重复/跳段** —— 原文明确 `no RTP origin reset` 且 `protocol window remains`。所以 **WAC 允许运行中改，不只在会话边界**。
>    → **风险在你们这边**：AF 的 `base_ns` 是 `send_packets` 的局部变量（`session.rs:1041`）。**一旦在改 lead 时同时改 `base_ns`，对端立刻看到映射跳变** —— 那才会造成"跳一段"。**把 lead 与 `base_ns` 解耦**，这是 P0-2 的实现关键。
> 3. **2000 ms 上限合理但偏大**。WAC 的自适应范围是 `latencyMin + 100 ms`。**建议上限 = 对端的 `latencyMax`**，而不是固定 2000 —— 超了就是"对端缓冲区装不下"，一定会爆。

### Q22. 自适应阶梯的具体数值 `[已确认]`

`LatencyPolicy` 目前用：单只 HomePod ALAC/PTP 起点/下限 50、阶梯 50/60/80/120/200；PCM/PTP 起点 60、下限 45；NTP 起点 200、下限 120；立体声对（原生/软件）起点=下限 120。上调条件是「一分钟内 3 次时间线恢复」，每分钟最多一级。

**需要确认**：

1. 报告里的「50/40/30 阶梯，验证下限 50」自相矛盾（下限 50 就到不了 40/30）。到底是**起点 50 只能往上**，还是**下限 30、50 只是推荐起点**？
2. 立体声对 120 ms 的下限是硬结论还是保守估计？有没有更小的已验证值？
3. 上调判据用「恢复次数」对不对？WAC 是用欠载次数、p99 迟到，还是重传率？

> **答复**：**"自相矛盾"的真相是：WAC 没有阶梯，40/30 存在但永远用不到。**
>
> 1. **不是"起点 50 只能往上"，也不是"下限 30"**。`CreateTopologyPlan` 只给**候选列表**（solo HomePod = 50/40/30），但 `floor = max(max(floorClass, tdev.Low+16), …)`。**floorClass = 50 会把 30/40 顶回去**。
>    → 所以 30/40 是给 UI/DAC 留的补间值，**实际生效的 floor 是 50**。你们看到的就是这个。
>    → **建议：直接删掉 40/30**，只留 `floorClass`。你们现在的 50/60/80/120/200 阶梯比 WAC 更合理。
> 2. **立体声对 120 ms 是硬结论**：`HomePodStereoPairMinLeadMs = 120`，在 `CreateTopologyPlan` 分支里强制。**没有更小的已验证值**（声明的常量在多处被**内联**，所以你会看到"常量存在但没人引用"的假象）。
> 3. **上调判据：不是"恢复次数"**。WAC 用统计量：`tdev.Value`、`DPath`、`JNet`、`Rtx`、`Lateness.P99_9`、`Sigma`。
>    → 你们的"1 分钟 3 次恢复"保留可以（那是策略层，WAC 没给），但**必须加上 p99 lateness**（WAC 公式里的 `L` 项）。原因很直接：**"恢复次数"对慢性迟到完全不敏感** —— 而慢性迟到正是 P0-1 的盲区。

### Q23. 16 bit 输出要不要抖动？ `[已确认]`

我们加了 TPDF 抖动（±1 LSB），但**数字静音保持精确 0**（接收端遮盖逻辑依赖全零帧判定）。

**需要确认**：WAC 是否也抖动？如果它不抖动，我们和它的听感会有细微差异（我们的本底噪声高约 1 bit，但没有量化失真）。若 WAC 用的是噪声整形（error feedback），请给出阶数/系数。

> **答复**：见 Q14。**WAC 抖动，但那是 WASAPI 16 bit 原生路径的副产物，不是它自己的算法。**
>
> 补三点：
> - WAC 位深**恒定 16 bit**，没有"抖动/不抖动"的开关。
> - 没有噪声整形（error feedback）的阶数/系数可见 —— **不要去找，它没有**。
> - **"数字静音保持精确 0"是 AF 的约束，不是 WAC 的**。等比对比没有意义；你们的理由是"接收端遮盖逻辑依赖全零帧判定"，这条成立就够，不需要 WAC 背书。

### Q24. `DATA_DISCONTINUITY` 怎么判真伪？ `[部分确认]`

我们的做法：记录上一包的设备位置 `position + frames`，若本包 `position` 与之相等就判为「回环绕回」的伪标志，只计数不清队列；否则才是真断流（清队列 + 重置重采样器 + 打 splice 边界）。

**需要确认**：

1. WAC 用 `pu64DevicePosition` 连续性判，还是用 QPC 时间差 / 包间隔判？
2. 真断流时 WAC 会不会保留重采样器状态（避免滤波器瞬态），只打 splice 标记？我们现在是 `reset()`，可能有一下轻微「咔」。

> **答复**：见 Q13。**WAC 不做"保留重采样器状态"这档 —— 它整个端点都重开。**
>
> 因为真断流走的是 `「… ; reopening endpoint」`，端点重开 ⇒ 重采样器必然重建。所以在 WAC 里**不存在**"只打 splice 标记、保滤波状态"的选项。
>
> → **你们现在的 `reset()` 方向是对的**，缺的是"重开端点"这一档（见 `report/03 §3.5.3` 的三种"没声"分类）。

### Q25. 事件通道 body 的准确形状 `[已确认]`

我们的解析器按 `mrCommand` → `command` → `cmd` → `action` 顺序找键，字符串形式（`play`/`pause`/`toggle`/`next`/`previous`/`mute`/`volume_up`…）与 MRP 数字（0 Play、1 Pause、2 Toggle、3 Stop、4 Next、5 Previous、6 Shuffle、7 Repeat、29 Mute、30 VolUp、31 VolDown）都接受；`volume` 支持 0–1、0–100、dBFS 三种归一化。整个事件以 `remote_command` 转发给桌面端。

**需要确认**：

1. HomePod 按暂停时实际发的是哪个键、什么类型（整数还是字符串）？body 是单个 dict 还是数组？
2. 数字编码是否就是 MediaRemote 的枚举（0=Play…）？我们用的映射表对不对？
3. `volume` 的线上单位到底是 0–1 还是 dBFS？AirFlash 现在只有「设备音量 SET_PARAMETER」，没处理过事件里的音量。
4. 我们只是把命令转发给桌面端、由 C# 去驱动系统媒体控制。WAC 是直接调 MediaRemote/SMTC 还是自己合成键？如果它用 `SendInput` 发 `VK_MEDIA_PLAY_PAUSE`，我们照抄更省事。

> **答复**：**1/2/3 见 Q11；第 4 条可以直接照抄。**
>
> 1. 键名 7 个（Q11），body 是**单个 dict**。HomePod 按暂停实际发哪个键**无法静态确定**（要看真机抓包）。
> 2. `mrCommand` 是**整数**，WAC **透传**、无映射表。你们的 MRP 枚举表与公开约定一致，**保留 + 未知值忽略并计数**。
> 3. **音量不走 Events**（Q11），走独立隧道。AF 不处理事件音量是对的。
> 4. **WAC 用 `SendInput` + `VK_MEDIA_*`，不是 MediaRemote COM API** —— 可以直接照抄，比调 COM 简单得多：
> ```csharp
> // WinAirCast.decompiled.cs:34899-34905
> public const byte VK_MEDIA_NEXT_TRACK = 176;   // 0xB0
> public const byte VK_MEDIA_PREV_TRACK = 177;   // 0xB1
> public const byte VK_MEDIA_STOP       = 178;   // 0xB2
> public const byte VK_MEDIA_PLAY_PAUSE = 179;   // 0xB3
> // :34914 声明，:35033 / :35035 调用
> SendInput(1, new INPUT[1] { input }, Marshal.SizeOf<INPUT>());
> ```
> 另外 `MediaCommand` 在 C# 里出现 42 次，说明**命令映射是在托管层做的**，引擎只负责把 plist 解成结构体。这个分工可以照搬。

### Q26. QoS 标记用哪条路？ `[已确认]`

我们要给音频 UDP socket 打 DSCP EF（46），两条路：

| 方案 | 优点 | 代价 |
| --- | --- | --- |
| `setsockopt(IPPROTO_IP, IP_TOS, 0xB8)` | 一行、无状态 | Windows 上常被忽略；只能标 socket，不分流向 |
| qWAVE（`QOSCreateHandle` + `QOSAddSocketToFlow`） | Wi-Fi 上真能拿到 WMM 优先级 | 需要 handle 生命周期管理，失败时要能优雅退化 |

**我们暂定**先做 `setsockopt` 版本（best-effort，失败只记遥测）。

**需要确认**：WAC 用的是 qWAVE 还是 `IP_TOS`？如果 qWAVE，`QOSFlowRate` / `ServiceType` 之类的参数它怎么填？

> **答复**：**WAC 用 qWAVE，不是 `IP_TOS`。`DSCP`/`IP_TOS` 在二进制里 0 命中。**
>
> 字节级证据（`crates\airplay-audio\src\qos.rs:115`）：
> ```
> qwave.dll
> QOSCreateHandle
> QOSCloseHandle
> QOSAddSocketToFlow
> QOSRemoveSocketFromFlow
> 「Windows QoS: qWAVE is not available; audio is sent without voice priority」
> 「… marked as voice traffic」
> ```
>
> - **`ServiceType` = voice**（"marked as voice traffic" ⇒ `QOSTrafficTypeVoice`），**不是 EF/DSCP 46**。
> - **失败可优雅退化**：`qWAVE is not available; audio is sent without voice priority` —— 正是你们想要的行为。
>
> **建议照抄这个结构**：
> 1. `LoadLibrary("qwave.dll")` 失败 → 打一条警告，继续跑（不阻塞音频）；
> 2. 成功 → `QOSCreateHandle` → 每个音频 socket `QOSAddSocketToFlow(QOSTrafficTypeVoice)`；
> 3. 析构 → `QOSRemoveSocketFromFlow` + `QOSCloseHandle`。
>
> **不要先用 `setsockopt(IP_TOS)`**：Windows 上基本被忽略，等于没做，而且会给人一种"已经标了"的错觉。

---

### Q27. BMCA 让位要不要做、怎么做？ `[已确认]`

我们已经按 IEEE 1588 解析并比较了 Announce 的 `(priority1, clockClass, accuracy,
variance, priority2, grandmasterIdentity, stepsRemoved)`，快照里能看出对端是否
「比我们更有资格当 master」（`better_master`）。

**需要确认**：

1. WAC 在发现更好的 master 时会真的让位吗？让位时机是「收到第一个更好的
   Announce」还是「连续 N 个」？N 是多少？
2. 让位过程中音频会断吗？WAC 是「停止发 Announce + 重新 SUBSCRIBE 对端
   Sync/Follow_Up」，还是先建新时钟再切？
3. 立体声对里两只 HomePod 各自 Announce 不同 grandmaster 时，WAC 怎么处理
   （取较优的一方？还是坚持自己的时钟以保证两只是同一时间轴）？
4. `stepsRemoved` 在 WAC 的比较里到底参不参与？（标准里只在同一 grandmaster
   的路径间使用）

> **答复**：**会让位 —— 但是在 SETUP 协商期一次性决定，运行中从不重新比较。**
>
> 〔串〕完整协商流程（`crates\winaircast-backend\src\session\timing.rs` 与 `airplay-timing`）：
> ```
> BMCA transport starting (pid=<n>)
> BMCA: Starting Mac-style negotiation with <ip> (our priority1=<n>)
> BMCA: Received Announce from <ip>: priority1=<n>, clock_id=<id>
> BMCA: Ignoring PTP general from <ip> (not master <id>)
> BMCA: no Announce from <ip> within <n>; re-probing (attempt <k>)
> BMCA: probe sent to <ip>, waiting for remote Announce (attempt <k>)
> BMCA: no Announce from <ip> <k> attempt(s); giving up
> BMCA: Decision <verdict> our_p1=<n>, remote_p1=<n>, remote_is_master=<bool>
> BMCA: receiver did not provide a usable winning clock (priority1=<n>, clock_id=<id>)
> BMCA: Yielded master to remote, transitioning to slave mode
> BMCA: Entering slave loop, syncing to <ip> (filtering other peers)
> BMCA slave: Sent Delay_Req (seq=<n>) / Received Sync / Received Follow_Up
> ```
>
> ## 逐条回答
>
> **1. 会让位；判据是 `priority1`，时机是 SETUP 期。**
> 决定点是一条日志：`BMCA: Decision <verdict> our_p1=<n>, remote_p1=<n>, remote_is_master=<bool>`。
> → **比较的是 `our_p1` 与 `remote_p1`**，加一个 `remote_is_master` 布尔；**没看到 `stepsRemoved`/`clockClass` 参与**（见第 4 条）。
> → 时机：`Starting Mac-style negotiation` → 发 probe → 收 `Announce` → `Decision` → 可能 `Yielded master to remote` → `Entering slave loop`。**全程在 SETUP 之前/SETUP 期**，`RECORD` 还没发生。
> → **运行中不会重新协商**：一旦 `Entering slave loop`，代码只处理 `Delay_Req / Sync / Follow_Up`（外加按 `not master <id>` 过滤其他 peer），**没有任何"再次比较 priority1"的串**。
> → 所以问题里的「N」**不是"连续 N 个更好的 Announce"**，而是**探活重试次数**：`no Announce from <ip> within <n>; re-probing (attempt <k>)` 直到 `no Announce from <ip> <k> attempt(s); giving up`。`<n>`（等待窗口）和 `<k>`（次数）都是运行时参数，**静态判不出具体值**；建议 `n≈1 个 Announce 周期`、`k=3`（与你的 Q17 暂定一致）。
>
> **2. 让位不会中断音频 —— 因为它在有音频之前就做完了。**
> 协商发生在 SETUP 期、`RECORD` 之前，此时没有 timeline、没有包在发。所以 `Yielded master to remote` 是**无感的**。
> 唯一会断音的是另一条路径：`「Rebuilding the playing group onto <x>」`（`timing.rs:548/549`）—— 那是**组成员变化**触发的组重建，不是 BMCA 让位。
> → 所以"先建新时钟再切"这个二选一不成立：WAC 的让位是**在无音频窗口内完成的**，不需要平滑切换。
>
> **3. 两只 HomePod 报不同 grandmaster —— WAC 只认一个，其余过滤。**
> 关键串：`「BMCA: Entering slave loop, syncing to <ip> (**filtering other peers**)」`、`「BMCA slave: Ignoring event from <ip> (not master <id>)」`、`「BMCA: Ignoring PTP general/event from <ip> (not master <id>)」`。
> → 它选出**唯一赢家**（`usable **winning** clock`），其余 peer 的 Announce/Sync/Follow_Up **一律按 `not master <id>` 丢弃**。
> → 所以不是"取较优的一方"，也不是"坚持自己"，而是 **先选出唯一 master，再把其他全部过滤掉**；如果**没有任何可用赢家** → `receiver did not provide a usable winning clock` → **保持自己是 master**（`Yielded` 不执行）。
> → 这个设计的好处正是你担心的那点：**同一时刻只有一个时间轴，立体声对不可能分歧**。
>
> **4. `stepsRemoved` 不参与决定。**
> 决定日志只有 `our_p1` / `remote_p1` / `remote_is_master` 三个量。`stepsRemoved` 作为 token 存在（解析了），但**不在 Decision 里出现**；`priority2` 全库 0 命中。
> → 你第 4 条的判断（stepsRemoved 只在同一 GM 的路径间用）方向对，但 **WAC 干脆没用它** —— 它的"Mac-style negotiation"是**简化的单键比较**。
>
> ## 给 AirFlash 的落地规格（可直接照抄）
>
> 你把「保持自己是 master」的暂定划掉了，那就按这个来做 —— **成本比你原先估的低一个数量级**，因为你担心的那两件事在 WAC 路线里都不存在：
> - 「切换会移动所有成员的 timeline」→ 只发生在**运行中**切换；放在 SETUP 期就没有 timeline 可移。
> - 「要重发 `SETRATEANCHORTIME`」→ 该命令在 WAC 二进制里 **0 命中**，它压根不发。
>
> ### 状态机（四态）
>
> ```
> Idle ──SETUP 开始──► Negotiating ──Decision: 我们更优──► Master   → 正常 SETUP/RECORD
>                            │
>                            └──Decision: 对端更优──► Slave    → (Yielded) → slave loop
>                            └──探活耗尽/无可用赢家──► Master   (fail-open)
> ```
>
> ### 时序（严格在 SETUP 之前或之中，`RECORD` 之前完成）
>
> ```
> 1. 选定 PTP mode（拓扑决定）        → 决定要不要协商
> 2. 发 probe / 等 Announce            → 窗口 N，重试 k 次
> 3. 收到 Announce(priority1, clock_id)→ 记录候选
> 4. Decision: remote_p1 < our_p1 ?     → 唯一判据
> 5. 让位 → 进入 slave loop（Delay_Req / Sync / Follow_Up）
>    不让 → 进入 sender master（Announce / Sync / Follow_Up / Signaling）
> 6. 选不出赢家 → 回 Master（fail-open），并打一条 `no usable winning clock`
> ```
>
> ### 必须照抄的三条硬规则
>
> | # | 规则 | 理由（WAC 的依据） |
> |---|---|---|
> | 1 | **判据只比 `priority1`**，不要引入 `clockClass`/`priority2`/`stepsRemoved` | `BMCA: Decision` 只带 `our_p1`/`remote_p1`/`remote_is_master`；`priority2` 全库 0 命中 |
> | 2 | **进 slave 后过滤所有非赢家**（按 `not master <id>` 丢弃） | `Entering slave loop, syncing to <ip> (filtering other peers)` |
> | 3 | **选不出赢家就保持 master**（fail-open） | `receiver did not provide a usable winning clock` |
>
> ### 明确不要做
>
> - ❌ 运行中重算 `priority1`（WAC 在 `Entering slave loop` 之后没有任何再比较的串）
> - ❌ 让位时重发 SETUP 或 `SETRATEANCHORTIME`
> - ❌ 让位时重建组 —— 那是另一条路径（`Rebuilding the playing group onto <x>`，组成员变化才触发，会断一下）
>
> ### 一个可以省掉的担心
>
> 你原来写「没有第二台 HomePod 可验证」—— 这条现在**不构成阻塞**：让位只影响 **WAC/AF 自己把谁当 master**，不需要第二台设备参与测试。用一台 HomePod + 一个能构造更好 `priority1` 的假 Announce 源（本机 loopback + 手写 PTP 包）就能验完整个状态机。

---

---

## 九、捕获自愈（P3-8）

### Q28. 多进程回环能否按 QPC 时间戳逐样本相加？ `[已确认]`

P3-8 现在对同一可执行文件的多个根实例各开一个 process loopback client，解码后按
`GetBuffer` 的 QPC 时间戳落到同一个采样网格上相加，再进重采样器。

**为什么拿不准**：没有真机，也没有两个实例同时发声的离线环境；不同
`ActivateAudioInterfaceAsync` client 的 QPC 基准是否严格一致、是否各自带不同的
额外延迟，只能静态推测。若基准差超过半个样本，相加会变成两路错位混音而不是
「采样级相加」。是否需要改成「以第一个实例的包为时间基准、其余实例按包序号
对齐」或「只抓主实例」？

> **答复**：**WAC 没有"多 client 相加"这件事 —— 它的进程回环是单选 + 候选回退，而且默认走设备回环。你的担心成立，且比你想的更严重。**
>
> ## 一、WAC 的两个采集后端是互斥的
>
> 〔串〕
> ```
> wasapi-device-capture        ← 设备回环（默认端点）
> wasapi-process-capture       ← 进程回环
> default loopback '<name>'  /  device '<name>'
> Switching capture to default loopback
> Switching capture to device: <name>          （capture_eq.rs:103 / 116 / 136）
> ```
>
> **进程回环到底是"同时抓多个 PID"还是"选一个 PID + 候选回退"——静态定不了，两组串互相矛盾**：
>
> | 倾向「同时抓」 | 倾向「选一个」 |
> |---|---|
> | `Switching to per-process capture (PIDs: <list>)` | `Capture session <x> ended (<y>) - trying next PID candidate` |
> | `Per-process capture started for candidate PIDs <list>`（复数） | `Selected process PID <n> exited - waiting for a replacement` |
> | | `process PID <n> (candidate <k>)`（单数 PID + 候选序号） |
> | | `No process PID candidates supplied` |
>
> `trying next` 与 `waiting for a replacement` 读起来像**串行试用**；但 `started for candidate PIDs`（复数）读起来像**全量启动**。Rust 无源码、`active_sources` 语义也未定位，**我给不出结论**。
>
> → 但这一点**不影响下面的答案**：无论 WAC 是串行还是并行，它都**没有"把多个 client 的样本按 QPC 相加"**的代码证据。
>
> ## 二、QPC 到底能不能用来对齐（平台事实，非 WAC 证据）
>
> | 问题 | 答案 |
> |---|---|
> | 基准是否严格一致？ | **是**。`QueryPerformanceCounter` 是**全系统单调计数器**，所有 client 共用同一时间基。 |
> | 偏移有多大？ | **不是半个样本，可能是整个 period。** `IAudioCaptureClient::GetBuffer` 给的 `pu64QPCPosition` 是**该 client 自己缓冲里这帧被捕获的时刻**，它 = 该进程渲染延迟 + 该 client 协商到的 period。不同进程的 period 可以是 3 ms / 10 ms —— 10 ms @ 44.1 kHz = **441 样本**。 |
> | 能按 QPC 落网格做到采样级相加吗？ | **不能。** |
>
> 所以你的判断是对的，而且「超过半个样本就错位」这个门槛**远早于**真实偏移量。
>
> ## 三、三条路，按推荐度
>
> ### 首选：改抓**设备回环**
> 这正是 WAC 的默认路径。多个根实例都渲染到同一个默认端点，**OS 混音器已经按采样精确相加过了**。你抓设备回环就直接拿到那个混合结果，**根本不需要对齐**。
> 代价：会把别的 App 的声音也抓进来 —— WAC 接受这个代价（默认就是 device）。
>
> ### 次选：进程回环但**只抓一个**（WAC 的进程模式）
> 选一个 PID，静音就试下一个候选，退出就等替补，**不相加**。适用于"同一时刻只有一个实例在发声"。
>
> ### 最后：多 client 相加（你现在的做法）—— 如果非要
> 1. **QPC 只用于粗对齐（ms 级）和检测漂移**，不要用于采样对齐；
> 2. 启动时在一个非静音窗口上做一次**互相关**，求出整数样本偏移；
> 3. 之后每路用**独立重采样器**锁住这个偏移；
> 4. 必须有「某一路 rebuffer/重启就整体重算偏移」的逻辑 —— 那会改变它的 QPC 基准。
>
> ⚠️ **相加前必须做相位检查**：如果两路内容相关（同一首歌的两个实例），偏移错了会**梳状滤波/相位抵消** —— 听起来是"变薄"而不是"错位"，**极难被发现**。这是多 client 相加最危险的失败模式。
>
> ## 四、给 AirFlash 的具体建议
>
> **把"自愈"和"多实例"解耦** —— 这是你问题的真正病根：
>
> - **自愈**：设备回环本身就有完整的重开路径（`produced no audible samples for <n>ms after a data discontinuity; reopening endpoint`、`Default render endpoint changed while capture was silent; switching from <a> to <b>`、`Capture session <x> ended - reacquiring endpoint, retry <k>`）。**不需要多 client 就能自愈。**
> - **多实例**：交给 OS 混音器（设备回环）。
>
> ⇒ 结论：**默认走设备回环；仅在用户明确要求「只抓这个 App」时才切进程回环。** 这样 P3-8 的全部目标都达成，而 QPC 对齐这个坑从一开始就不存在 —— 不管进程回环内部是单选还是并行。

---

## 十、兼容 profile（P2-13）

### Q29. 能力预判要不要带上 `/info` 的 `model`/`sourceVersion`？ `[已确认]`

**我们暂定**：`receiver_capabilities` 的能力预判继续只用 `supportedFormats` +
桌面端下发的 legacy codec 列表 + 对端自述 `features`，不带 `/info` 的 `model` /
`sourceVersion`；只有在 `session.rs` 真正建会话时才用完整指纹。

**需要确认**：同一台 HomePod 如果 `/info` 有 bit40、而桌面端下发 `features` 缺
bit40，预判会把它的 `selected_timing` 算成 `ntp`，实际会话却按 `model` 走 PTP。
预判要不要也吃型号证据、与运行时保持一致？这会让 `receiver_capabilities` IPC
事件里 `selected_timing` 的取值发生变化。

**为什么拿不准**：Q18 的答复是「AF 应改用型号 + 拓扑表」，按字面预判也该带；
但 P2-13 是纯重构，带型号会改变现有预判的取值，超出「行为不变」的范围，
所以这轮先保持原状。

> **答复**：**WAC 没有"预判 vs 运行时"两条路径 —— 只有一个 `CreateTopologyPlan`，两边都调它。所以它不存在你要修的那种分裂。**
>
> ## WAC 的真实做法：两段式，证据源分开而且是有意的
>
> 〔码〕`WinAirCast.decompiled.cs:38404` `CreateTopologyPlan(devices, codec)` 全文就是一个决策表：
>
> ```csharp
> if (devices.Count == 0)                       return ("None", "Unknown", …);
> if (devices.Any(d => !d.SupportsAirPlay2))    return ("Classic RAOP", "NTP", …, false);   // ← 只看 TXT
>
> bool isAppleTv  = devices.Any(d => LooksLikeAppleTv(d.Model, d.Name));
> bool allHomePod = devices.All(d => LooksLikeHomePod(d.Model, d.Name));
> bool isPcm      = codec == "PCM";
>
> if (n==1 && isAppleTv)        return ("Apple TV solo",      "NTP", {100,80},    live);
> if (n==1 && allHomePod && pcm)return ("HomePod solo (PCM)", "NTP", {60,45,30},  live);
> if (n==1 && allHomePod)       return ("HomePod solo",       "PTP", {50,40,30},  live);
> if (n>1  && allHomePod)       return ("HomePod group",      "PTP", {120,110,100,90}, live);
> if (n>1  && isAppleTv)        return ("Mixed Apple group",  "PTP", {120,110,100}, live);
> if (n==1)                     return ("AirPlay 2 receiver", "PTP", {100,80,60}, live);
>                                return ("AirPlay 2 group",    "PTP", {180,160,140}, live);
> ```
>
> **两段，证据源严格分开**：
>
> | 段 | 决定什么 | 证据源 |
> |---|---|---|
> | 第一段 | **是不是 AirPlay 2**（不是就走 Classic RAOP + NTP） | **只用 TXT**：`SupportsAirPlay2` = `features` 或 `ft` 的 **bit40** + `srcvers ≥ 354.54.6` |
> | 第二段 | AP2 里**走 PTP 还是 NTP**、lead 候选多少 | **只用 model/name**：`LooksLikeAppleTv` / `LooksLikeHomePod` |
>
> 第二段的判据（`LooksLikeHomePod`，`:38778`）：
> ```csharp
> if (!model.Contains("AudioAccessory")) {
>     if (NameDecides(model)) return name.Contains("HomePod");   // model 缺失/"Unknown" 时才看名字
>     return false;
> }
> return true;
> ```
> `LooksLikeAppleTv` 同构（`AppleTV` / `"Apple TV"`）。**model 优先，缺失或被写成 `Unknown` 才退回 name。**
>
> ## 为什么你担心的分裂在 WAC 里不会发生
>
> 注意第一段是 **`Any(d => !d.SupportsAirPlay2)` 全否**：只要有一台 TXT 里没有 bit40，**整个 plan 退化成 Classic RAOP + NTP**，**model 说什么都不管**。
>
> ⇒ **WAC 不是靠"两个来源一致"来避免矛盾，而是靠"只有一个来源"**：`SupportsAirPlay2` 是唯一入口，`selected_timing` 是它的下游。你描述的场景（`/info` 有 bit40 但下发 `features` 缺 bit40）在 WAC 里结果是 **Classic RAOP / NTP** —— 它会**降级**，不会偷偷按 model 走 PTP。
>
> ## 对 AirFlash 的建议
>
> **1. 「这轮先保持原状」是对的，理由要写对。**
> 理由不是"带型号会改变取值"（虽然是），而是 **WAC 的时序本来就是两段：AP2 与否在 TXT 定，timing 在型号定。** 你的 `receiver_capabilities` 只做第一段，不做第二段，这是**少做了一步，不是做错了**。
>
> **2. 想让预判与运行时一致，正确做法不是"预判也吃 model"，而是"两边调同一个纯函数"。**
> ```rust
> // 提议：把决策提成纯函数，预判与会话都调它
> fn select_timing(txt: &Txt, model: &str, name: &str, codec: Codec, count: usize) -> Timing
> ```
> 这样"预判带不带 model"就不再是个需要权衡的选项 —— 反正只有一处实现。这也是 WAC 的实际结构（`CreateTopologyPlan` 是 `static` 纯函数）。
>
> **3. 若 P2-13 真的要严守"行为不变"，就在代码里留一条注释**，把这个已知差异钉死，别让它变成日后的谜：
> ```rust
> // 已知差异（见 QUESTIONS Q29）：本预判只做 AP2/Classic 判定（TXT bit40 + srcvers），
> // 不做 PTP/NTP 分支（那需要 model/name）。运行时由 session.rs 用完整指纹决定。
> // WAC 的做法是把两段合成一个纯函数 CreateTopologyPlan；我们暂未合并。
> ```
>
> **4. 一个必须注意的差异**：WAC 第一段是 **`Any(!SupportsAirPlay2)` 全局降级**。如果你的立体声对/组里只有一台缺 bit40，WAC 会把**整个组**降成 Classic RAOP/NTP。这条比"预判取值变了"严重得多 —— 值得对照检查你现在是不是按成员逐台判的。
---

---

## 十一、抢占识别（P3-3）

### Q30. 真实接收器被接管时，最先出现的是哪种信号？ `[已确认]`

P3-3 现在把「未预期 TEARDOWN / RTSP 403 / `Connection: close`」都只当信号，
必须再配上「媒体在 3 秒内停流」或「PTP Announce 的 clock identity 改变 / 对端
停止交换」之一才判 `preempted`；只有信号时退回原来的通用故障。

**为什么拿不准**：没有真机抓包，无法确认 HomePod 被另一台控制器接管时到底发
TEARDOWN、回 403、直接断流，还是先改 PTP announce，也测不出这些动作之间的真实
间隔；3 秒窗口和「至少一个旁证」由本地模拟加现有故障节奏推出。是否应放宽到
5 秒，或把「随后重新 announce」单独视为充分条件？

> **答复**：**WAC 不靠 TEARDOWN/403/close 判抢占 —— 它的权威判据是 `statusFlags`；探测失败反而「照样回收」。你现在的「信号 + 旁证」结构方向反了。**
>
> ## 一、WAC 的三层，先看第一层
>
> 〔串〕`session/lifecycle.rs:205/215` —— **回收前先做一次 live status probe**：
> ```
> HomePod <ip> is free (live statusFlags=<n>); reclaiming
> HomePod <ip> is playing another sender's session (statusFlags=0x<n>); checking again in <n>s
> HomePod <ip> status probe failed (<err>); reclaiming
> HomePod <ip> status probe timed out; reclaiming
> ```
>
> 这四条把判据说得很清楚：
>
> | 探测结果 | 动作 |
> |---|---|
> | `statusFlags` 表明空闲 | **立刻回收** |
> | `statusFlags` 表明「另一个 sender 在播」 | **不做判定，等 `<n>s` 后再探**（不是失败，是排队） |
> | 探测**失败** | **照样回收**（fail-open） |
> | 探测**超时** | **照样回收**（fail-open） |
>
> ⇒ **探测不成功 ≠ 放弃回收**。这一点和 AF 现在「拿不到旁证就退回通用故障」正好相反。
>
> ## 二、第二层：控制面症状只是线索，且带完整上下文
>
> 〔串〕`session/tick.rs`
> ```
> Encrypted control EOF observed for <ip> (policy=<x>, rtp_healthy=<b>, packets_prepared=<n>)
> HomePod control poisoned by preemption with active audio
> HomePod <ip> preempted while audio active (takeover <n>); reclaiming it once no other sender is playing
> ```
>
> 注意 `control EOF` 那条带了三个上下文：`policy` / `rtp_healthy` / `packets_prepared`。**它不是判定，是留证据** —— 判定发生在后面的 `statusFlags`。
>
> 另外 `control poisoned by preemption with active audio` 说明 WAC 会**把这条控制连接标记为中毒不可复用**，而不是重连同一根。
>
> ## 三、第三层：连续计数 + 时间窗 → 才认输
>
> 〔串〕
> ```
> HomePod <ip> taken over <n> times in a row, each within <n>s of a reclaim;
>   it is in use from another device, yielding
> ```
>
> 这是**整个答案的核心**：
> - 数的是 **`in a row`（连续）**，不是累计；
> - 每次都要求 **距上次 reclaim 在 `<n>s` 以内** —— 超过窗口就清零；
> - 达到阈值就 **`yielding`**：放弃，判定「这台真被别的设备在用」。
>
> 对应 [`Status] PREEMPTED` 的两个 reason（`events.rs:215` 邻域）：
> ```
> [Status] FAILED <…>
> PREEMPTED
> [Status] PREEMPTED <reason>      ← reason ∈ { takeover, limit }
> takeover
> limit
> ```
> `takeover` = 被接管；`limit` = 撞到连续上限。**两个都要报给用户**，因为处置完全不同。
>
> ## 四、直接回答你的两个问题
>
> ### 「3 秒要不要放宽到 5 秒？」→ **不要。这个维度选错了。**
> WAC 里根本没有「媒体在 N 秒内停流」这条规则。它用的是 **`statusFlags` 轮询 + `checking again in <n>s`**。
> 两者的差别很重要：
> - 「媒体停流 N 秒」是**间接症状**，正常换歌、暂停、切歌都会触发；
> - 「`statusFlags` 显示另一 sender 在播」是**直接事实**，不会误判。
>
> ⇒ 别调窗口，**换成查 `statusFlags`**。窗口参数只保留一个用途：连续计数的时间窗（见下）。
>
> ### 「随后重新 announce 算不算充分条件？」→ **不算，而且这是最危险的一条。**
> 重新 announce 只证明「对端还活着」，**不证明「它空闲」**。被接管后对端当然还在 announce —— 它正在为**新的** sender 服务。
> 若把 re-announce 当充分条件，AF 会**在被占用时反复抢**，正是 WAC 用 `taken over in a row` 计数器要避免的拉锯。
>
> ## 五、给 AirFlash 的改造建议
>
> ### 1. 把判定换成 `statusFlags`（这是唯一需要新增的能力）
> AirPlay 的 `statusFlags` 在 `/info` 和 mDNS TXT 里都有。WAC 还额外打 `Receiver /info statusFlags changed since discovery for '<ip>': mDNS=0x<a>, live=0x<b>` —— **它明确知道两边可能不一致，并选择信 live 的那份**。
> ⇒ AF 也应该：**信 live 探测，不信 mDNS 缓存**。
>
> ### 2. 加连续计数 + 时间窗 + yielding
> ```rust
> struct TakeoverState { consecutive: u32, last_reclaim: Instant }
> const LIMIT: u32 = 3;            // 具体值需实测；WAC 的这个数我没静态拿到
> const WINDOW: Duration = Duration::from_secs(30);
> ```
> 语义严格照抄：
> - 每次 reclaim 后，若**在 `WINDOW` 内**又发生 takeover ⇒ `consecutive += 1`；
> - 超过 `WINDOW` 才再次 takeover ⇒ `consecutive = 1`（**重置，不是清零后不加**）；
> - `consecutive >= LIMIT` ⇒ `yielding`，**停止抢占**并报 `PREEMPTED(limit)`。
>
> ### 3. 清理掉「只有信号就退回通用故障」
> 改成：
> ```rust
> match probe_status(ip) {
>     Ok(sf) if free(sf)          => reclaim(),
>     Ok(sf) /* busy, other sender */ => schedule_retry(AFTER),   // 不是失败
>     Err(_) | Timeout            => reclaim(),                    // fail-open
> }
> ```
> **`Err`/`Timeout` 走 reclaim** 是关键 —— 别让它掉进通用故障分支。
>
> ### 4. 控制连接标记中毒，别重连同一根
> `control poisoned by preemption with active audio` ⇒ 抢占后这条 RTSP 连接**不可复用**，必须新建（这也和 Q16 的 `Encrypted RTSP cannot be re-keyed mid-session` 一致）。
>
> ## 六、没拿到的东西（† 静态不可判定）
>
> | 项 | 状态 | 怎么定死 |
> |---|---|---|
> | 连续上限的具体数值（`taken over <n> times`） | † 运行时参数，未定位到常数池 | 反汇编 xref 到该格式串，或抓日志看阈值 |
> | 时间窗的具体秒数 | 同上 | 同上 |
> | `checking again in <n>s` 的间隔 | 同上 | 同上 |
> | `statusFlags` 的位定义（哪个 bit = 正在播放） | † 二进制里没有位名表 | 抓 HomePod 空闲/播放中的 `/info` 对比 |
>
> 但这四个数**都不影响结论**：结构是「statusFlags 判定 + 连续计数限流 + fail-open 回收」，参数只是标定。

---

### Q31. 真机上 `/info` 的 `statusFlags` 如何取值，探测在接管瞬间是否仍可用？ `[部分确认]`

P3-3 已按 Q30 改成「live `GET /info` 的 `statusFlags` 判空闲 / 他人播放，
探测失败或超时 fail-open 回收」。但以下事实没有真机无法确定，代码里因此保留
`ANOTHER_SENDER_BIT = None`（在填上之前，每个格式正确的探测都只能得到 `Free`，
不会产生 `BusyElsewhere`）：

1. `statusFlags` 在 HomePod 空闲、AirFlash 自己播放、另一台控制器播放时分别是
   什么值？哪个 bit 表示「另一个 sender 正在播放」？`/info` 的该字段是否
   始终是 32 位无符号整数，还是某些型号是字符串/浮点？
2. 接管瞬间（接收器刚把会话交给别人）`/info` 是否仍能立刻应答？是否需要
   认证、会不会先回 403/454 或直接断开？这决定 `PROBE_TIMEOUT=2s` 是否够，
   以及 fail-open 回收会不会在接收器尚未完成切换时过早触发。
3. Q30 §6 没有静态恢复的四个运行时参数：连续上限、时间窗、
   `checking again in <n>s` 的间隔、探测超时。当前取
   `TAKEOVER_LIMIT=3` / `TAKEOVER_WINDOW=30s` / `REPROBE_DELAY=2s` /
   `PROBE_TIMEOUT=2s`，都是本地选择；真机日志能否给出更接近 WAC 的值？
4. WAC 的「live 与 mDNS 不一致时信 live」是否意味着接管后 mDNS TXT 仍可能
   报告旧值？AirFlash 目前不缓存 mDNS 的 `statusFlags`，只信 live，但需要在
   真机上确认这样不会在接管后读到过期的一份。

> **答复**：**第 1 问有硬答案 —— WAC 里就有位定义，`ANOTHER_SENDER_BIT` 不用猜。第 2、3、4 问确实要真机，但每一问我能给你「该测什么 + 判据长什么样」。**
>
> ## 一、`statusFlags` 的位定义：WAC 有，而且不止一位〔码〕
>
> `WinAirCast.decompiled.cs:30026` 有一整个类专管这件事：
>
> ```csharp
> public static class ReceiverStatusFlags
> {
>     public const ulong StereoPartnerUnreachable = 16384uL;   // 0x4000  bit14
>     public const ulong ReceiverSessionActive    = 131072uL;  // 0x20000 bit17
>
>     public static bool IsStereoPartnerUnreachable(ulong sf) => (sf & 0x4000)  != 0;
>     public static bool IsReceiverSessionActive(ulong sf)   => (sf & 0x20000) != 0;
> }
> ```
>
> 同一文件 `:29981` 还有一个：
> ```csharp
> public static class ReceiverAccessPolicy
> {
>     public const ulong PasswordRequiredFlag = 128uL;          // 0x80    bit7
>     public static ReceiverAccess Classify(ulong sf, bool advertisesPassword, int? acl) {
>         if (advertisesPassword || (sf & 0x80) != 0L) return PasswordRequired;
>         if (acl.GetValueOrDefault() != 2)            return Open;
>         return HomeMembersOnly;
>     }
> }
> ```
>
> | bit | 常量 | 含义 |
> |---|---|---|
> | **7** | `PasswordRequiredFlag` | 需要密码 |
> | **14** | `StereoPartnerUnreachable` | 立体声配对伙伴不可达 |
> | **17** | `ReceiverSessionActive` | **接收器上有活动会话** |
>
> **类型是 `ulong`（64 位无符号）**，不是 32 位。C# 侧 `DiscoveredDeviceInfo` 的字段就是 `ulong StatusFlags`（`:12425`）。
>
> ## 二、⚠️ 但 `0x20000` 不等于「**另一个** sender 在播」
>
> 这是最要命的一处语义差。`ReceiverSessionActive` 的字面意思是「**有**活动会话」——**包括你自己的**。
>
> WAC 怎么区分？**它不靠单独的 bit，靠上下文。** 看它那条日志：
> ```
> HomePod <ip> is playing another sender's session (statusFlags=0x<n>); checking again in <n>s
> ```
> 「another sender's」这个判断发生在**回收流程里**（此时它本来就没持有会话），而不是靠某个 bit。
>
> ⇒ **AirFlash 的正确用法**：
> ```rust
> const STATUS_FLAG_RECEIVER_SESSION_ACTIVE: u64 = 0x20000;
>
> fn classify(sf: u64, we_hold_session: bool) -> Probe {
>     if sf & 0x20000 == 0 {
>         Probe::Free                       // 无人占用
>     } else if we_hold_session {
>         Probe::OursActive                 // 就是我们自己在用 —— 不是抢占！
>     } else {
>         Probe::BusyElsewhere              // 我们没会话但它有 ⇒ 别人在用
>     }
> }
> ```
> **你现在的 `ANOTHER_SENDER_BIT = None` 可以直接填 `0x20000`，但必须配上 `we_hold_session` 这一维**，否则会把自己正在播的会话误判成被抢占 —— 那会立刻触发一轮"回收"，把好好的会话打断。
>
> ## 三、第 2 问：接管瞬间探测还能不能用
>
> † 静态不可判定（需要真机）。但我能从 WAC 里给你**它假设了什么**：
>
> 〔串〕
> ```
> Receiver /info for '<ip>' has unexpected field types (<err>); continuing without the live probe
> ```
> ⇒ WAC 对 `/info` 的**任何解析异常都是容错的**，退回用发现阶段的数据继续。这就是 fail-open。
>
> 〔串〕`/info` 是**不认证**的裸 `GET`（同 `GET_PARAMETER`、`OPTIONS` 一个层），不走 HAP/FairPlay。
>
> **所以 `PROBE_TIMEOUT = 2s` 够不够，判据不是"够不够快"而是"答错了会怎样"**：
>
> | 情况 | 2 s 是否安全 | 理由 |
> |---|---|---|
> | 接管中 `/info` 仍答**旧值 `Free`** | **不安全** —— fail-open 会立刻回收，可能撞上正在切换的接收器 | 这是 2 s 真正的风险，不是超时 |
> | `/info` 直接超时 | 安全 | 走 fail-open 回收，行为与 WAC 一致 |
> | `/info` 回 403/454 | 安全 | 同上 |
>
> ⇒ **真正的防线不是调 timeout，是「回收前再确认一次」**。建议：
> ```rust
> // 探测说 Free，但先别急着 SETUP —— 隔一个短延迟再探一次
> if probe()? .is_free() {
>     sleep(CONFIRM_DELAY);            // 建议 500 ms，远小于 2 s
>     if probe()?.is_free() { reclaim() }
> }
> ```
> 这一手 WAC 没有（它是单次探测决策），但**代价极小、收益明确**，而且它不改变 fail-open 语义 —— 只是把"过早回收"的概率压下去。
>
> ## 四、第 3 问：四个运行时参数
>
> † 静态拿不到（已确认：`WINAIRCAST_` 环境变量里没有 takeover 相关项，二进制里 `WINAIRCAST_TAKEOVER` 0 命中）。
>
> 但你现在的本地选择**有一处值得改**：
>
> | 参数 | 你现在 | 我的意见 |
> |---|---|---|
> | `TAKEOVER_LIMIT` | 3 | **合理**。WAC 的语义是"连续 N 次"，3 是常见工程值。保持。 |
> | `TAKEOVER_WINDOW` | 30 s | **合理**。窗口要显著大于一次 reclaim+播放的周期，30 s 站得住。 |
> | `REPROBE_DELAY` | 2 s | **建议放宽到 3–5 s**。WAC 的 `checking again in <n>s` 是"排队"，不是"快速重试"。2 s 太急，会在对方刚建立的会话上反复敲门。 |
> | `PROBE_TIMEOUT` | 2 s | **保持**，但配上面第三节的二次确认。 |
>
> **真机日志怎么给出更准的值**：让 AF 打一行带上下文的回收日志（照抄 WAC 的字段），跑几次真实接管后直接看分布：
> ```
> takeover probe: ip=<ip> sf=0x<n> we_hold=<b> probe_ms=<n> decision=<free|ours|busy|err> consecutive=<n> since_last=<ms>
> ```
> 有了 `probe_ms` 的 p99，`PROBE_TIMEOUT` 就有依据；有了 `since_last` 的分布，`TAKEOVER_WINDOW` 就有依据。
>
> ## 五、第 4 问：mDNS 会不会报旧值
>
> **会。WAC 那条日志就是证据**〔串〕：
> ```
> Receiver /info statusFlags changed since discovery for '<ip>': mDNS=0x<a>, live=0x<b>
> ```
> 两边**确实会不一致**，而且 WAC 专门为此打了一条日志 → 说明这是**常态**，不是异常。
>
> ⇒ **AirFlash「只信 live、不缓存 mDNS」的做法是对的**，不用改。而且这就是 WAC 的选择（它打印差异后以 live 为准）。
>
> 唯一的补充：**发现阶段仍然要用 mDNS 的 `sf`/`flags` 做初判**（决定要不要显示"可连接"），只是**决策回收时必须重新 live 探测**。别把两处混成一处。
>
> ## 六、一句话总结
>
> `0x20000` 填进去，但**加一维 `we_hold_session`**；`PROBE_TIMEOUT` 不用改，**加一次二次确认**；`REPROBE_DELAY` 从 2 s 放宽到 3–5 s；mDNS 只用于发现、live 只用于决策 —— 这两条记得别串。

---

### Q32. 真机上 bit17 会不会在自己播放时置位？「TEARDOWN / 403 ⇒ 会话已不属于我们」成立吗？ `[部分确认]`

P3-3 已按 Q31 的位定义实现三分类：`statusFlags & 0x20000 == 0` ⇒ `Free`
（回收）；置位且 `we_hold_session` ⇒ `OursActive`（`FallThrough`，不回收）；
置位但不持有 ⇒ `BusyElsewhere`（3 s 后再探）。`we_hold_session` 由两项本地
证据推出：① `rtp_healthy`（最近 2 s 内成功发过 RTP）；② 接收器没有结束过我们
的会话（未预期 `TEARDOWN` 或 `403` 之后，本成员永久记为已结束，直到回收重建）。

没有真机，以下三点不能定死：

1. HomePod 在**我们自己播放**时是否真的把 bit17 置位？如果**不置位**，我们自己
   的健康会话会得到 `Free` 并照旧回收（与改造前行为相同），`OursActive` 分支
   等于没生效；如果**置位**，Q31 担心的误判才真正被挡住。空闲、自己播放、被另一
   台控制器接管三种状态各采一次 `/info`，看 `statusFlags` 的十六进制值即可。
2. 「未预期 `TEARDOWN` / `403` ⇒ 会话已不属于我们」这个推断是否成立？有没有
   接收器在会话仍属于我们时回 403（例如限流或对该请求返回不支持）？若存在，
   结果只是把 `OursActive` 变成 `BusyElsewhere`（多排队 3 s，不会误回收），
   但会影响回收时机。
3. 接管瞬间 `/info` 是否仍可读、`statusFlags` 是否已经是新状态？如果接收器在
   接管后短暂回旧值（bit17 仍置位），我们会判成 `OursActive` 并暂停探测，直到
   下一个控制面信号；如果短暂清零，我们会按 `Free` 立刻回收。前者是 Q31 §三
   「二次确认」想解决的问题，本次仍未实现。

> **答复**：**第 2 问有硬答案 —— 在 WAC 的分类里 403 是「访问/认证」错误，根本不是「会话不属于我们」的信号。而且你这个设计有一个会导致永久卡死的死锁，见 §五。**
>
> ## 一、第 1 问：bit17 在「我们自己播放」时置不置位
>
> † 静态不可判定（真机行为）。但我能给你**一个不需要写代码、30 秒就能测完**的办法 —— 而且比我上一轮建议的更好：
>
> **`statusFlags` 同时存在于 mDNS TXT 的 `sf` / `flags` 里。** 所以只要用任意 mDNS 浏览器看就够：
>
> ```bash
> # macOS
> dns-sd -L "<HomePod 名>" _airplay._tcp local.
> # 或直接看 TXT
> dns-sd -B _airplay._tcp local.
> ```
> 在「空闲」「AF 正在播」「iPhone 接管中」三个时刻各看一次 `sf=` 的十六进制值。
>
> ⇒ 你甚至不用先实现探测就能拿到答案。**建议先做这一步再写 `ANOTHER_SENDER_BIT`。**
>
> ## 二、第 2 问：403 能不能当「会话已不属于我们」—— **不能，WAC 明确把它归到别的类**
>
> 〔串〕`airplay-client/src/connection.rs:1021` 附近的错误分类：
> ```
> <method> rejected with status <n>
> access denied by receiver (HTTP 403)
> <…> requires on-screen PIN pairing (HTTP 470)
> <…> rejected by receiver (HTTP <n>)
> ```
>
> 〔串〕`auth.rs`：
> ```
> authentication rejected
> verification failed
> pairing failed
> invalid signature
> Receiver refused PIN pairing (HTTP 403). On macOS, check AirPlay Receiver access and password settings
> requires on-screen pin pairing
> ```
>
> 〔串〕backend 的错误枚举（`session/codec.rs` 邻域）把 `403` 和这些并列：
> ```
> 403  pin  pairing  timed out  feedback  connection refused  network unreachable
> broken pipe  reset by peer  i/o error  os error  415  unsupported …
> ```
>
> **WAC 的模型里，403 = 「接收器拒绝了你的访问」**（密码/权限/PIN），`470` = 需要屏幕 PIN。
> 它**从不**把 403 解释成「会话被抢走了」。
>
> ⇒ **你那条「未预期 `TEARDOWN` / `403` ⇒ 本成员永久记为已结束」的推断，方向是错的。**
> 403 最可能的原因是**认证/权限状态变了**（HomePod 固件更新后要求密码、macOS 侧关了访问控制等），跟抢占无关。
>
> ## 三、WAC 真正的「媒体是否还在流」判据：**RTP 增量窗口**
>
> 〔串〕`session/tick.rs` 的 Control EOF 日志 —— 这才是你要抄的东西：
> ```
> Control EOF <…>: elapsed_ms=<n>, packet_delta=<n>, expected_~<n>,
>                   underrun_delta=<n>, retransmit_delta=<n>, rtptime=<n>
> 2 s gap window <…>
> (no teardown; discontinuity would show as packet_delta expected or underrun_delta>0)
> ```
>
> 注意两点：
> 1. **窗口就是 2 s** —— 和你现在用的 2 s 一致，这个数是对的；
> 2. 它判「媒体真的停了」靠的是 **`packet_delta` / `expected` / `underrun_delta` / `retransmit_delta`**，不是靠 RTSP 状态码。
>
> 〔串〕`session/control.rs` 的控制毒化决策矩阵：
> ```
> Apple TV group secondary control poison (receiver session dead, rtp_healthy=<b>)
> Apple TV group primary control poison (receiver may be silent, rtp_healthy=<b>)
> Solo Apple TV control poison and RTP unhealthy: <…>
> Solo Apple TV control poison with healthy RTP <…> deferring teardown: <reason>
> control dead and RTP path unhealthy
> control degraded and RTP path unhealthy
> Apple TV group secondary control degraded budget exhausted
> ```
>
> ⇒ **控制面出问题 + RTP 健康 ⇒ 延迟 teardown**；**RTP 也不健康 ⇒ 才拆**。
> 这和你 `we_hold_session = rtp_healthy` 的直觉**完全一致** —— 这一维你选对了。
>
> ## 四、第 3 问：接管瞬间 `statusFlags` 是新值还是旧值
>
> † 不可判定。但 WAC 的行为给了两个间接线索：
>
> 〔串〕`is playing another sender's session (statusFlags=0x<n>); checking again in <n>s`
> ⇒ WAC **假定它会变，并靠重探兜底**。它没有"读一次就信"。
>
> 〔串〕`Receiver /info statusFlags changed since discovery for '<ip>': mDNS=0x<a>, live=0x<b>`
> ⇒ 它**同时持有两份并对比**，说明两边都可能停滞。
>
> ⇒ 你 Q31 §三 想要的那个「二次确认」在这里**确实还是必要的**，而且现在有了第二个理由：不只是防「假 Free」，也防「假 Busy」。
>
> ## 五、⚠️ 你这个设计有个死锁，真机上一定会撞
>
> 你的规则是：**「未预期 TEARDOWN 或 403 之后，本成员永久记为已结束，直到回收重建」**。
>
> 把它和 §二 的结论连起来：
>
> ```
> 403 到达（真实原因：认证/权限，会话其实还是我们的）
>   → we_hold_session = false   （永久，直到回收）
>   → 下次探测：statusFlags & 0x20000 != 0（因为会话确实是我们的）
>   → 但我们不持有 ⇒ 判 BusyElsewhere
>   → 3 s 后再探
>   → 还是 BusyElsewhere …… 永远
> ```
>
> **它永远等不到 `Free`，因为那个 bit 是你自己会话的置位；而 `we_hold_session` 被永久锁死为 false —— 于是永不回收。**
>
> WAC 用「连续计数 + yielding」正是为了打破这种僵局：**数到上限就重新判定，而不是永远排队**。
>
> ### 修法（二选一，建议都做）
>
> **① `we_hold_session` 必须可自愈，不能 latch。**
> ```rust
> // 不要永久记，改成按 RTP 健康度实时推导 + 有限期信任
> fn we_hold_session(rtp_healthy: bool, ended_at: Option<Instant>) -> bool {
>     if rtp_healthy { return true; }                       // RTP 还在发 ⇒ 肯定是我们的
>     match ended_at {
>         Some(t) => t.elapsed() < Duration::from_secs(2),  // 2 s 内信这个结论
>         None    => true,
>     }
> }
> ```
> 注意第一行：**RTP 健康就无条件认为会话是我们的**，这比任何控制面信号都硬。403 到达但 RTP 还在出包，就是 WAC 那句 `control poison with healthy RTP → deferring teardown`。
>
> **② `BusyElsewhere` 也要计数，撞上限就强行回收。**
> ```rust
> // 排队次数上限；到顶就当作 Free 处理（fail-open），报 BusyElsewhereExhausted
> if busy_retries >= BUSY_LIMIT { reclaim(); }
> ```
> 这是 WAC 的 `taken over in a row … yielding` 的镜像：它能认输，你也要能**脱身**。
>
> ## 六、一句话总结
>
> | 你的设计 | 结论 |
> |---|---|
> | `rtp_healthy` 当 `we_hold_session` 的主要证据 | ✅ **对**，与 WAC 的 `control poison + healthy RTP → defer` 一致 |
> | 2 s 窗口 | ✅ **对**，WAC 的 `2 s gap window` 就是它 |
> | 「403 ⇒ 会话已结束」 | ❌ **错**，WAC 把 403 归为认证/访问错误；这是死锁的源头 |
> | 「永久记为已结束」 | ❌ **必须改成可自愈**，否则永远卡在 `BusyElsewhere` |
> | `BusyElsewhere` 无限重试 | ❌ **要加上限**，否则没有出口 |
> | 接管瞬间二次确认 | ✅ **仍需要**（防假 Free，也防假 Busy） |

---

### Q33. 组内时钟域：Mixed Apple group 到底该 PTP 还是 NTP？ `[待答复]`

P2-13b 已把协商改成「先探测全组、再算一个 `TimingPlan`、最后逐个 SETUP」。
实现采用的规则是：

- 任一成员没有 AirPlay 2 证据（bit40 且 `srcvers >= 354.54.6`）→ 整组 NTP；
- 都在 AP2 分支内 → 任一成员单独会选 NTP 就整组 NTP，否则整组 PTP
  （即「组内取最保守」）；
- 单成员完全维持 97 行表刻画的原有行为。

这修掉了「一台有 bit40、一台没有 → 一个 PTP 一个 NTP」的分裂，但也和 WAC
报告里的一条拓扑结论不一致，需要确认：

1. **Mixed Apple group（HomePod + Apple TV，两台都有完整 AP2 证据）**：报告说
   WAC 的第二阶段按型号/名称给出「Mixed Apple group → PTP」，而 AF 的 97 行表
   里 Apple TV 单机是 NTP。AF 现在按「保守取 NTP」处理，整组会退到 NTP。
   正确做法是跟随 WAC 的 PTP，还是保持保守 NTP？如果把 Apple TV 放进 PTP 组，
   它是否必须作为 PTP follower 跟发送端时钟（即使它单机时走 NTP）？
2. **组内是否允许每成员 timing 不同**：AF 现在坚持「一组一个时钟域」，
   组计划 NTP 时显式 PTP 会被降级、显式 NTP 会被折进组计划，显式 buffered
   或 `follow_receiver_clock` 在组 NTP 下直接报错。WAC 的 `CreateTopologyPlan`
   之后，是否可能给不同成员下发不同的 `timingProtocol`（例如 HomePod 对 PTP、
   Apple TV 对 NTP）？如果是，AF 的「一组一时钟域」需要放宽到什么程度？
3. **告警可见性**：`group_timing_divergence` 在 SETUP 之前发出，而桌面端
   `SessionController` 会丢弃 `startedAt == 0` 之前的 warning（`startedAt` 在
   `streaming`/`standby` 时才置位）。这条告警在真实 UI 上是否应该可见？
   应该改成 `streaming` 后补发，还是让桌面端对 `channel=setup` 的 warning
   放宽过滤？WAC 有没有对应的用户可见提示或日志？
4. **真机验证 AP1 + AP2 混组**：整组 NTP 时，那台本来单机走 PTP 的 AP2 成员
   （HomePod）是否会接受 NTP 会话？AF 依据的是 WAC 第一阶段
   `Any(!SupportsAirPlay2) → Classic RAOP/NTP` 的整组降级；需要真机确认
   HomePod 在组里不会坚持自己的 PTP grandmaster，也不会因此静音。

> **答复**：（待补充）

---

## 答复区

> 已在每条条目下方就地回填（`> **答复**`）。下面是需要**优先改代码**的 4 条，按影响排序。

### 必须改（会做错）

| # | 你们的做法 | WAC 的实际做法 | 后果 |
|---|---|---|---|
| **Q8 / Q21** | 改 lead 时**同时平移 sync 的 networkTime** | 只改本地 playout lead；`advertised latencyMin remains`、`no RTP origin reset` | 对端看到映射跳变 → **跳一段/播两次** |
| **Q12** | 2 s 轮询 + **300 ms 去抖** | 2 s 定时器，**没有 300 ms 去抖**；封面**一律重编码** + 4 MiB 封顶 + Q95 | 多加了一层延迟 |
| **Q18.3** | 用 `features bit41` 判 PTP | **只用 bit40** 判"是否 AirPlay 2"，PTP 由**型号 + 拓扑**表决定 | 判错设备能力 |
| **Q9.2** | 低于 `floor=50` **直接拒绝** | **照样应用**，只打 `applied exactly as you measured it` | 用户量到 45 却用不了 |

### 建议改（能少走弯路）

| # | 建议 |
|---|---|
| **Q2** | 引擎**无限重锚 + 只报 `deadline_resets`**；"3 次/10 s"放策略层 |
| **Q5** | 死区 **enter 5 ms / exit 1 ms**（非对称）；断点是**整机重置**不是换基线；baseline 用**中位数**不是首样本 |
| **Q15** | 别无条件 `AUTOCONVERTPCM`：**原生优先、失败回退**，并打印失败原因 |
| **Q25.4** | 直接抄 `SendInput` + `VK_MEDIA_*`（`:34899-34905`），别调 MediaRemote COM |
| **Q26** | 直接上 **qWAVE**（`QOSTrafficTypeVoice` + 优雅退化），别先做 `IP_TOS` |
| **Q27** | **让位可以做，而且很便宜**：WAC 把它压在 **SETUP 期**（无 timeline/无 RECORD）→ 天然无断音。判据只看 `priority1` + `remote_is_master`；选不出赢家就保持 master；进 slave loop 后**过滤所有非赢家**。`better_master` 只上报也可以，别在运行中切 |

### 三条最有价值的新发现

1. **伺服增益链路把两件事对上了**：`correction_ppb = filtered_ns / 20`，夹 **±50 ppm**。而 AF 现成的重采样环量程是 **±500 ppm** —— 你们的环路**量程绰绰有余，缺的只是"死区 + 增益"**。P0-3 的改造量比报告估计的更小。
2. **`deadline` 是唯一权威源**，WAC 因此只需要改一处；AF 的 `base_ns` 是局部变量（`session.rs:1041`），这是所有"映射跳变"类问题的**同一根因**。
3. **`ComputeRecommendation` 是一次算三方案，不升档**：所以"自适应阶梯"整套是 AF 的净增能力，不必也不该去对齐 WAC 的候选表（那张表里有 30/40 这种**永远用不到**的值）。

---
