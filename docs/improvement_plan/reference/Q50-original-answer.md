# Q50 外部参考答复原文

归档日期：2026-11-01。来源：工作区已有的 Q50 答复；以下原文保留用于追溯。
[返回问题清单](../QUESTIONS.md)，见 Q50 的 AirFlash 落地说明。

原答复的源码行号对应旧快照。以下内容不能直接视作当前项目规格：

- 原文把卡顿前证据窗口写为 250 ms；当前是 1 s。250 ms 是重传排空统计窗口，也是证据采样的最小间隔。
- 原文采用约 350 包/秒的说法；本项目固定 352 帧/包时，44.1 kHz 约为 125.3 包/秒，48 kHz 约为 136.4 包/秒。
- 原文关于启动期、200 包阈值及误判率的确定性表述没有本项目设备实测支持；当前只将这些数值作为待校准的分类策略。
- 当前实现使用相对稳态重传包比例，而非原示例中的未定义 `RTX_FRACTION_LIMIT`；缺失卡顿前基线时默认归入 `sender_stall`。
- 窗口是否排空通过发送线程直接读取队列长度与丢弃计数，不依赖稍后生成的 JSON 快照；提前关闭的窗口也记为下界。

---

> **答复**：三问都能答，但先要纠正三处前提——其中两处会改变你们现在写下的代码。

---

#### 先纠三处前提

**① 分子用错了。** `rtx_fraction = rtx / packets_sent` 里的 `rtx`，如果取的是
`retransmit_requests`（`transport.rs:405`），那它是**NACK 消息条数**，一条消息可以
请求几十个包。真正的分子是 **`retransmit_requested_packets`**（`transport.rs:406`，
`+= u64::from(count)` 累加的就是包数）。用消息数当分子，`rtx_fraction` 会系统性
**偏小一个数量级**，10 s 基线上更容易读成「链路没问题」。

**② `pre_stall_underrun` 的符号是反的，应当从传输判据里删掉。**
`live.rs:184-224`：

```rust
let missing = force_silence || s.frames.is_empty();
...
} else {
    underrun |= !force_silence;      // ← 采集队列空
...
if underrun { s.metrics.underrun_packets += 1; }
```

`underrun_packets` 记的是**采集队列取空了**，即**发送端没东西可发**——这是
**发送端饥饿**的症状，不是传输失败的证据。它确实可能是「发送线程被重传拖慢」
的下游结果，但**单次计数无法区分因果**，而且阈值 `>= 1` 低于噪声地板：一次
GC、一次缺页、一次线程抢占就能产出 1。

⇒ 把 `pre_stall_underrun` 降级成独立的 `sender_starved` **展示标记**，不参与
`reason` 选择。理由和 [14 §14.5.6] 那条一样：**一个能同时由两侧原因产生的计数，
不能当判决依据。**

**③ `pending.len()` 已经暴露了，不用改 transport。** `transport.rs:487`：

```rust
v["retransmit_pending"] = json!(self.pending.len());
v["buffered_pending"]  = json!(self.buffered.as_ref().map_or(0, |s| s.queued()));
```

⇒ 直接读 metrics 快照里的 `retransmit_pending`。

---

#### 问 1：「会话 < 10 s」要不要一律压成 `sender_stall`？

**方向对，但不能无条件**——会瞎掉对真死链路的判断。

三个判据度量的其实不是同一件事：

| 判据 | 度量什么 | 失效模式 |
|---|---|---|
| `rtx_fraction` | 稳态窗口内**平均**丢包强度 | 跨度短 ⇒ 噪声；分母 0 ⇒ 未定义 |
| `pre_stall_rtx >= 30` | 卡顿前 250 ms 的**瞬时**强度 | 启动期缓冲填充本身就产 rtx |
| ~~`pre_stall_underrun >= 1`~~ | **发送端**供给不足 | 噪声地板以下，且因果反向 |

「10 s 下限」的理由是**因果先验**（前 10 s 的卡顿几乎必然是启动期的），不是
**统计有效性**。这两件事正交，所以不能用一个阈值同时表达。

建议**双阈值**：

```rust
let age = now - session_start;
let reason = if age < Duration::from_secs(10) {
    // 启动期先验成立，但极端值压不住
    if pre_stall_rtx >= 200 { Reason::ConfirmedTransportFailure }
    else                    { Reason::SenderStall }
} else if span >= Duration::from_secs(10) && steady_packets_sent > 0 {
    let frac = steady_requested_packets as f64 / steady_packets_sent as f64;
    if frac >= RTX_FRACTION_LIMIT { Reason::ConfirmedTransportFailure }
    else                          { Reason::SenderStall }
} else {
    // 会话够长，但稳态窗口不完整（分母为 0 或跨度不足）
    if pre_stall_rtx >= 30 { Reason::ConfirmedTransportFailure }
    else                   { Reason::SenderStall }
};
```

**为什么 200 而不是 30**：250 ms 内 200 个包请求 ≈ 800 req/s，而 44.1 kHz 立体声
ALAC 约 350 包/s —— 超过总包速率两倍，只可能是大量重复请求或链路真死。**启动期
缓冲填充产生不出这个量级**，所以它不会被启动期噪声触发，能安全地穿透「< 10 s
一律 sender_stall」这条规则。

**代价对比**：

| 方案 | 漏判真死链路 | 误判启动期抖动 |
|---|---|---|
| 一律 `sender_stall` | **会**（3 s 会话真死也报发送端） | 0 |
| 一律绝对判据（现状） | 0 | **会**（30 个启动期 rtx 就报传输失败） |
| **双阈值** | 仅 `30~200` 这段 | 仅 `> 200` 这段 |

---

#### 问 2：`capped` 靠什么判定？

**不用推断，用 `retransmit_pending`。**

```rust
// 在 250 ms 窗口关闭的那一个采样点上取，不要事后补读
let capped = metrics.retransmit_pending > 0;
```

为什么比「最后一次迭代是否还在收请求」严格更好：

- 它是**队列长度**，排空了就是 0，**没有概率问题**——你们算出的 0.6% 误标率是
  代理本身的代价，而精确信号没有这个代价；
- 你们的代理有**系统性偏置**：链路越坏越容易误标成「下界」，**而这恰恰是最需要
  读准的时候**。

**⚠️ 一个残留竞态**：读完 `pending.len()` 到上报之间队列可能变化。所以必须在
窗口关闭的**同一个采样点**取快照。`transport.rs:487` 就在 metrics 快照里，天然满足。

**另外还有一条更强的信号你们没提**：`MAX_PENDING = 512`（`transport.rs:27`），
满了会丢请求并记 **`retransmit_queue_drops`**（`:411` / `:617`）。

> **窗口内 `retransmit_queue_drops` 增长 ⇒ 不只是 `capped`，是队列本身溢出了。**

这比 `capped` 强一个量级，应该单独上报（比如 `capped = PendingCapped | QueueOverflow`），
因为「排空了没排完」和「根本装不下」对用户是两件事：前者等一会就好，后者意味着
重传窗口设小了。

---

#### 问 3：实测跨度要不要不按时间裁剪？

**「按 30 s 除」本身就是要修的那个 bug，不是裁剪的问题。**

你们写的现象——「会话只有 12 s，按 30 s 除会把 1.5× 读成 3.75×」——根源是
**除以了常数 30**。正确做法只有一条：

```rust
let span = (newest.timestamp - oldest.timestamp).as_secs_f64();
// 永远除实测跨度，不除常数
let frac = requested_packets as f64 / packets_sent as f64;   // 分母已是实测发送量
```

会话 12 s ⇒ 除 12 ⇒ 读 1.5× ✓；会话 30 s ⇒ 除 30 ⇒ 读 1.5× ✓。**这样"跨度上限"
就不再影响正确性，只影响基线长度。**

**关于「改成取采样环最旧样本、不按时间裁剪」**：`/tmp/airflash` 快照里搜不到 `121`，
这个环是你们新写的，我没法核。但可以给个判据：

- 若 `121 × 采样间隔 ≈ 30 s`（例如 250 ms × 121 = 30.25 s），那**环本身就是 30 s 环**，
  「按时间裁剪」和「取最旧」结果几乎相同，**改了没有收益**；
- 只有当采样间隔远小于 250 ms（比如每迭代 ~3 ms，121 个样本 ≈ 0.36 s）时，
  取最旧才有意义——但那时真正的问题是**环太小**，应该加长环而不是改裁剪。

**结论：先修除法。** 除法修好后，30 s 基线已经远超 10 s 下限，精度足够；
跨度是否超 30 s 不再是正确性问题。

**唯一必须补的是跨度下限**：`span < 10 s` ⇒ 比值不可用 ⇒ 走问 1 的绝对兜底路径。
这和问 1 是同一件事的两面——**「比值什么时候可用」= 「跨度够不够长」**，
而「会话够不够长」是另一条独立的因果先验。

---

#### 最终判定表（可直接照抄）

| 条件 | 判据 | 产出 |
|---|---|---|
| `session_age < 10 s` 且 `pre_stall_rtx >= 200` | 绝对（极端） | `confirmed_transport_failure` |
| `session_age < 10 s` 其余 | — | `sender_stall` |
| `span >= 10 s` 且 `steady_packets_sent > 0` | `requested_packets / packets_sent` | 按阈值二选一 |
| 其余（会话长但窗口不完整） | `pre_stall_rtx >= 30` | 二选一 |
| `retransmit_pending > 0` @窗口关闭 | 正交标记 | `capped` |
| `retransmit_queue_drops` 增长 | 正交标记 | `queue_overflow`（更强） |
| `underrun_packets` 增长 | **仅展示** | `sender_starved`，**不参与 reason** |

**三处都只改边界写法，不改方向**——和你们自己的判断一致。但前提 ①② 是真会做错的：
分子取错会让比值小一个数量级，`underrun` 当传输证据会把采集侧问题报成网络问题。

> **对报告的影响**：[12 §12.3] 里「`pre_stall_underrun >= 1`」这条需要同步删掉，
> 理由见上面 ②。我会一并修正。
