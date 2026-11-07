# SAMA 3.4" 水冷屏 0xCC 增量帧 / 0xD0 位置信息 协议与状态机还原报告

> 目标：在不启动官方 `SAMA.exe`、不每帧重连串口的前提下，用自研 C# 服务实时刷新屏幕。
> 方法：静态反编译（`E:\AIWorkSpace\decompiled`，ILSpy 输出）+ 串口抓包（`E:\AIWorkSpace\spy\spy_serial.log`，205 MB / 485,527 行）双向交叉验证。
> 未运行官方 GUI、未连接硬件、未修改任何既有源码。本报告是本目录唯一新增文件。

---

## 0. 结论速览（TL;DR）

1. **物理链路不是瓶颈**：协议名义 115200 8N1，但实测整帧 921600 B 只需 **≈0.57 s（≈1.6 MB/s）**，CDC 波特率被设备忽略。实时刷新可行。
2. **命令包固定 250 B**：`[cmd][EF 69][param BE32][flag][00 00][payload…0x00]`，**payload 从偏移 10 开始，容量 240 B**（不是"8 字节保留"，见 §10 修正）。
3. **原始数据包 = 249 B 数据 + 1 B 0x00**，每包 250 B；数据流长度由命令的 `param` 给出，最后一包用 0 补齐。
4. **0xCC 增量帧** = `[4B seq][4B posLen]` 头（8 B payload）+ `delta || posInfo || EF 69` 原始流；
   - `param = len(delta) + len(posInfo) + 2`（含 EF69 尾）
   - `seq` 从 **0** 开始，每成功提交一帧 **+1**；整帧重载后 **归零**
   - `posLen` 只统计 posInfo（不含 EF69）；静态主题恒为 **0**
5. **0xD0 位置信息**只在"差分/视频模式"（`isDiiferInite=true`）出现，**紧跟在 0xCA 整帧之后**，不是每个 0xCC 都发；
   - `param = len(posInfo) + 2`，payload 为空，随后是 `posInfo || EF 69`
   - 注意：0xD0 的 posInfo 由**整帧图像**算出，0xCC 的 `posLen` 由**差分基准帧**算出，二者长度**不一定相等**（4 个视频会话中 2 个巧合相等、2 个不等，见 §7.2）
6. **needReSend:1 的处理 = 整帧重来**，不是重发同一条增量：置 `reSendInite`，下一轮重新发 **0xC8（或 0xCA）×2 + 归零 seq**，最多连续 5 次（`renderFirstCnt>5` 抛异常、计入 `ERRCNT` 并重连）。`renderCnt` 只用于视频**卡顿判定**（两次查询不变 → 重启视频/整帧重载），不直接参与重发决策。
7. **0xCF 在每一帧的 0xCC 之前**发送（同一轮 `QuerryResend()` 内 `0xCF → await read`），是 async 等待但不长阻塞（通常读到上一块已就绪应答）；整个循环被**节流到约 1 帧/秒**。
8. 静态主题官方顺序（抓包实证）：`0x01 → 0x79 → 0x96 → 0x7B → 0x7D → 0x86 → [0x2C×250] → 0x7B → 0xC8 + 921600 B ×2 → 循环(0xCF → 0xCC)`。
   差分/视频模式把 `0xC8` 换成 `0xCA`，并在每个 `0xCA` 后追加 `0xD0`。

---

## 1. 五个问题的直接回答

| # | 问题 | 结论 |
|---|------|------|
| 1 | 0xCC 之前是否必须先发 0xCA/0x86/0xC8？顺序？ | **必须先有整帧**。每个会话实测都是 `0x96 → 0x7B → 0x7D → 0x86 → 整帧×2 → 0xCF/0xCC…`。静态主题用 `0xC8`（回 `full_png_sucess`），差分/视频模式用 `0xCA`（回 `seq_png_init_sucess`）+ 每帧 `0xD0`。`0x86`(SetMonitorStart) 只发一次，`0xCA` 在静态主题里**从不出现**（全日志 8 个 0xCA 全部在 4 个视频会话中）。 |
| 2 | 0xCC 8 字节头 `[seq][posLen]` 来源/初值/递增？ | `seq = Monitor.imgSeqIndex`（`long`，截断为 BE32 写入），初始化/整帧重载后 **=0**；每次 `ShowSeqImage` 成功后 `imgSeqIndex++`，并把当前帧入队作为下一帧差分基准。`posLen = posInfo 长度`，仅当 `NeedPosInfo=true` 时非 0，否则写 0。 |
| 3 | 0xD0 与 0xCC 的先后、param、是否每帧都要？ | 0xD0 只跟在 **0xCA 整帧**之后（每个整帧一次，`SetPngPosInfo` 内），在后续 0xCC 之前。`param = posInfoLen + 2`（含 EF69），payload 空，随后发 `posInfo||EF69` 原始流。**不需要每帧都发**；静态主题完全不发（`NeedPosInfo=false`，posLen=0）。 |
| 4 | needReSend:1 重发同一帧还是下一帧？renderCnt？ | **重发整帧（关键帧），seq 归零，然后继续**。官方不会重传丢失的那条增量。最多连试 5 次，超过抛 `renderFirstCnt err` → `ERRCNT++` → 重连。`renderCnt` 只用于视频卡顿检测（`lastVideoCnt == curVideoCnt` → `reSendInite=true` 且返回 false）。 |
| 5 | 0xCF 在哪、是否阻塞？ | 在 `RenderFrame` 每帧的开头、`ShowSeqImage` 之前；`QuerryResend()` 内 `send 0xCF(207,1)` → `await 读应答`（超时 1000 ms，重试 1 次，中间 500 ms）。是 await，串行化该帧管线，但通常立即拿到上一块应答，不产生长阻塞。若超时返回 `""`，被当作"无冒号应答"容忍，连续 4 次才判定失败。 |

---

## 2. 证据链与分析方法

| 证据 | 内容 |
|------|------|
| `decompiled\됒.cs` | 传输层：组包 `_A9B7(int cmd,int param,byte[] payload,byte flag)`（行 2725）、原始流发送 `_A9B7(byte[],int)`（行 2855）、读应答 `_A9B7(bool)`（行 1687）/`_A9B7(int,bool,int)`（行 1653）、常量 `m_ꔂ=249, m_ꗳ=250`（行 3219-3220） |
| `decompiled\뽞.cs` | 设备实现 `ShowFullImage`(6977)、`ShowSeqImage`(6769)、`SetPngPosInfo`(6574)、`QuerryResend`(7074)+状态机 `ꪨ`(2191)、`SetMonitorStart/Stop`(6440/6461)、`WaitMediaStop`(7449) |
| `decompiled\UsbMonitorL\Monitor.cs` | 渲染状态机 `목`：整帧重载路径、0xCF 查询、`ShowSeqImage` 调用、seq 递增/归零、1 s 节流 |
| `decompiled\깬.cs` | 差分编码器 `ꗳ(byte[],byte[],int,int,out int,int)`(5812)、posInfo 编码器 `ꔂ(byte[],int,int,out int,int,int)`(5412) |
| `strings_decrypted.txt` | 解密后的常量：`needReSend:1`、`renderCnt:`、`theme:`、`|`、`full_png_sucess`、`seq_png_init_sucess`、`renderFirstCnt err` 等 |
| `spy\spy_serial.log` | 19 个会话、4,644 个命令包、2,215 对 0xCF/0xCC、2,295 个应答块，用于验证所有推论 |

日志行格式：`HH:MM:SS.mmm [tid] W SerialPortStream 250 <hex> |<ascii>|`、`W-ok`、`RA <len> <hex> |ascii|`。

---

## 3. 链路层帧格式（已用 4,644 个包验证）

### 3.1 命令包（固定 250 B，所有命令都写满 250 B）

```
偏移  长度  内容
0     1     cmd
1     2     0xEF 0x69              ← 固定同步字
3     4     param (uint32 BE)
7     1     flag
8     2     0x00 0x00              ← 恒为 0（4,644/4,644 个包验证）
10    ≤240  payload（不足补 0x00）
```

- 4,644 个命令包的 `byte[8]=byte[9]=0` 无一例外；`flag` 只有 `0x78`(play video) 用过 1，其余全 0。
- 全日志 241,616 条写记录 **全部是 250 B**（`packet length distribution: [('250', 241616)]`）。

### 3.2 原始数据包（249 + 1）

```csharp
// 됒._A9B7(byte[] data, int len) 行 2855
array = new byte[250];
for (int off = 0; off < len; off += 249) {
    int n = Math.Min(249, len - off);
    Array.Copy(data, off, array, 0, n);   // 余下字节保持 0
    port.Write(array, 0, 250);            // 永远写 250 B
    if ((off+249) % 24900 == 0) Thread.Sleep(1);
}
```

- `param` = 数据流总字节数；包数 = `ceil(param/249)`；线上字节 = 包数×250。
- 末包不足 249 B 的部分为 0，**第 250 字节恒为 0**。
- 发送端每 100 包（24,900 B）插 1 ms 让路。

### 3.3 实测吞吐（重要）

| 场景 | 数据量 | 包数 | 线上字节 | 耗时 | 速率 |
|------|--------|------|----------|------|------|
| 0xC8 整帧 | 921,600 | 3,702 | 925,500 | 572 ms | ≈1.6 MB/s |
| 0xCC 典型增量 | 9,388 | 38 | 9,500 | ~1 ms | — |
| 0xCC 空帧 | 8 | 1 | 250 | <1 ms | — |

> 结论：`115200 8N1` 只是 CDC 的"名义"参数，设备并不按位速率限流。整帧 0.6 s 是**设备处理/渲染**时间，不是链路时间。新服务不要按 11.5 KB/s 估算。

### 3.4 应答（设备 → PC）

- 设备按块返回，每块通常 **1024 B**（`RA 1024 …`），以 `0x00` 填充；`0x01` 的应答是精确 24 B 字符串。
- 客户端用 `ReadTimeout=100 ms` 的 `Read(1024)` 读，`UTF8` 解码后取到第一个 `0x00` 为止。
- **应答无帧头、无长度前缀**，只能靠"一次查询一次读"配平；块会排队，见 §8.4 的 1 块延迟。

---

## 4. 命令集（与显示链路相关）

| cmd | 名称（来自代码） | param | payload | 后续原始流 | 典型应答 |
|-----|------------------|-------|---------|-----------|----------|
| 0x01 | IniteDev / 握手取版本 | 1 | `C5 D3` | — | `chs_34inch.dev1_rom1.88` |
| 0x64 | 未知（仅视频会话） | 1 | — | 1 包（1 B 有效+补零） | `775510` |
| 0x6E | 播放视频（设路径） | path.Length | UTF8 路径 | — | `play_video_success` |
| 0x78 | 播放视频（flag=1） | path.Length | UTF8 路径 | — | `play_video_success` |
| 0x79 | StopVideoPlay | 1 | — | — | — |
| 0x7B | SetBrightness | 1 | `[level]`（实测 `AA`） | — | — |
| 0x7D | SetConfig | 5 | `[bright,startMode,0,imgFlip,sleepDelay]` | — | — |
| 0x81 | SetRotate | 1 | `[r]` | — | — |
| 0x82 | StopApp | 1 | — | — | — |
| 0x83 | Shutdown | 1 | — | — | — |
| 0x84 | Reboot | 1 | — | — | — |
| 0x86 | **SetMonitorStart** | 1 | — | — | — |
| 0x87 | SetMonitorStop | 1 | — | — | — |
| 0x96 | **WaitMediaStop 查询** | 1 | — | — | `media_stop` |
| 0xC8 | **整帧（普通模式）** | 921600 | — | 921600 B BGRA | `full_png_sucess` |
| 0xCA | **整帧（差分/视频模式）** | 921600 | — | 921600 B BGRA | `seq_png_init_sucess` |
| 0xCC | **增量帧** | 增量总长 | `[seq][posLen]` | delta‖posInfo‖EF69 | 无直接应答（靠 0xCF） |
| 0xCF | **状态查询** | 1 | — | — | `needReSend:0\|renderCnt:N[\|theme:…]` |
| 0xD0 | **位置信息** | posInfoLen+2 | — | posInfo‖EF69 | — |

> 0x00 / 0x44 各出现 1 次，语义未定，不影响显示链路。

---

## 5. 会话状态机（从代码 + 抓包还原）

### 5.1 启动序列（每次建链一次）

```
IniteDev:      0x01(1) payload C5 D3        → 读应答 "chs_34inch.dev1_rom1.88" → romVersion=1.88
WaitMediaStop: 0x79(1)                      // 先停视频
               0x96(1) → 读 "media_stop"     // 最多 20 轮，失败则 0x79+delay200 重试
SetBrightness: 0x7B(1) [bri=0xAA]
SetConfig:     0x7D(5) [AA 00 00 00 00]
SetMonitorStart: 0x86(1)
delay 100 ms
RenderFrame(first=true, query=true)
```

### 5.2 首帧（整帧重载路径，`flag7=true`）

```
renderFirstCnt++ ; if (renderFirstCnt > 5) throw "renderFirstCnt err"
꽝()            → 单包 250×0x2C（"逗号哑包"，用于唤醒/冲刷）
delay 10 ms
SetBrightness → 0x7B(1)[bri]
delay 100 ms
ShowFullImage(data, 921600, isDiiferInite)   # 第 1 遍：0xC8/0xCA + 921600 B 原始流
delay 100 ms
ShowFullImage(data, 921600, isDiiferInite)   # 第 2 遍（官方固定发两遍）
ꗳ(imgSeqIndex, currentFrameData)             # 入队作为差分基准
reSendInite = false
imgSeqIndex = 0
delay 10 ms
await read()                                  # 消化 ack 块
```

实测两次整帧间隔 678 ms = 572 ms 传输 + ~100 ms 延时，与代码完全吻合。

### 5.3 稳态循环（约 1000 ms/帧）

```
loop {
  await RenderFrame(first=false, query=true)   // 见 5.4
  elapsed = now - lastFrameTime
  if (elapsed < 1000) await Task.Delay(1000 - elapsed)   // Monitor.cs:10659-10694
}
```

### 5.4 单帧 RenderFrame 内部

```
if (reSendInite) goto 整帧重载路径(5.2)         // ← needReSend 的落点

renderFirstCnt = 0
seq = imgSeqIndex
if (queryEnabled /*A_1*/ && !_29AC()) {         // _29AC() 未完全还原；实测静态/视频均走此分支
    ok = await QuerryResend()                   // 0xCF → 读应答
    if (!ok) { reSendInite = true; reStartVideoMark = true; reSendCnt++; return false; }
}
if (videoPlaying && lastVideoCnt == curVideoCnt && !Util.quickTest) {  // renderCnt 未推进
    reSendInite = true; reStartVideoMark = true; return false;         // 视频卡死 → 整帧重载
}
if (currentFrameData != null) {
    n = ShowSeqImage(FrameQueue.Last().Value, currentFrameData, seq, needPosInfo)
    if (n > 0) { imgSeqIndex = seq + 1; ꗳ(imgSeqIndex, currentFrameData); }
}
```

关键点：

- 差分基准 `img_seq` = `FrameQueue.Last().Value`（上一帧**已成功提交**的画面），不是"设备当前画面"。失败帧不进队列，下一轮仍以同一基准重算。
- `needPosInfo = (currentTheme.videoPath != null && !_28BB())`，即**只有视频播放才需要位置信息**；静态主题恒 false。
- `ShowSeqImage` 内部还会按 romVersion 选编码器：`rom >= 1.89` 用 `깬.ꔂ(...)`，本机 1.88 用 `깬.ꗳ(...)`（旧格式，见 §6.4）。

---

## 6. 0xCC 增量帧

### 6.1 报文结构

```
命令包（250 B）：
  CC EF 69 | 00 00 24 AC | 00 | 00 00 | 00 00 00 00 | 00 00 00 00 | 00… 
  cmd sync  param=9388    flag  rsv    seq=0          posLen=0      补零
  param = 9388 = 9386(delta) + 0(posInfo) + 2(EF69)

原始流（38 包 × 250 B = 9,500 B 线上；有效 9,388 B）：
  delta[9386] || EF 69
```

- `param` 一定包含末尾 `EF 69`。
- payload 为 **8 字节**：`seq`(BE32) + `posLen`(BE32)，其余 232 B 补 0。
- `posLen` **不含** EF69（而 0xD0 的 param 含，见 §7.2 —— 这是必须照抄的不对称）。

### 6.2 seq 规则

| 时点 | seq |
|------|-----|
| 建链后 | `imgSeqIndex = 0` |
| 整帧重载（首帧或 needReSend 后） | 入队后立即 `imgSeqIndex = 0` |
| 第一帧 0xCC | 0 |
| 每次 `ShowSeqImage` 返回 >0 | `imgSeqIndex = seq + 1` |
| 空增量帧（param=8）也照样 +1 | 是（实测 seq 2 即空帧，seq 3 继续） |

实测：22:31 会话 seq 0…311（312 帧 / 5.3 分钟），22:37 会话 seq 0…；跨会话重新从 0 开始。

**全量校验（2,215 条 0xCC）**：

- `param` 与实际收到的原始字节数 **2,215/2,215 完全一致**；
- 载荷末尾 `EF 69` **2,215/2,215 全部存在**；
- 会话内 `seq` 严格 +1（2,196/2,196 次相邻比较通过），每次整帧后首个 CC **seq=0**（19/19 个会话）。

> `seq` 只被设备用作帧序号/丢失检测，**不需要跨会话连续**；服务重启后从 0 开始即可。

### 6.3 空增量（"无变化"）帧

编码器无差异时返回长度 0，`ShowSeqImage` 会构造 6 字节固定内容再补 EF69：

```
payload = 80 00 00 00 00 00 EF 69      param = 8
```

含义：一条"单像素记录"，位置 0、颜色 0、alpha 位=0 → 设备视为透明/无变化。
官方**照发不误**，且 seq 照常 +1（日志中大量 `param=8`，例如 22:31:04.466 的 seq=2）。
建议照抄：这样 seq 与设备侧计数始终对齐；是否"可以不发"未验证（见 §12）。

### 6.4 差分数据格式（旧编码器 `깬.ꗳ`，rom<1.89，本机适用）

记录流，无文件头：

| 形式 | 字节 | 说明 |
|------|------|------|
| 普通游程 | `[pos:3 BE][count:2 BE][count × (B',G',R')]` | `pos` = 起始像素下标（0..230399），`count` ≤ 65000 |
| 单像素 | `[pos:3 BE | 0x80][B',G',R']` | 位置最高位置 1 作标记（pos<0x40000，安全） |

像素 3 字节携带 4 bit alpha（量化）：

```
A  = 原 BGRA 的 alpha 字节
n8 = A >> 4                    // 0..15
B' = (B & 0xFC) | (n8 >> 2)    // 高 2 bit 存 alpha[7:6]
G' = (G & 0xFC) | (n8 & 3)     // 低 2 bit 存 alpha[5:4]
R' = R
```

- 像素"变化"判定：B/G/R/A 任一不同。
- `count` 超过 65000 → 编码器返回 null（长度 0）→ 退化成 §6.3 的空帧（**画面被静默丢弃、基准会漂移**，这是官方 `needReSend` 存在的根因之一）。
- 输出缓冲按整帧大小分配，`A_4` 返回实际写入长度。

> 现成实现：`E:\AIWorkSpace\SamaEncoder\SamaEncoder.exe`（反射调用 `깬.ꗳ`/`깬.ꔂ`）。自研服务可照上面 20 行逻辑重写，无需再反射。
> 编码器逐字节细节、边界情况与可直接编译的 C# 参考实现见同目录 **`encoder.md`**（本报告只保留协议视角的最小描述）。

### 6.5 分包

见 §3.2。注意 `param` 与原始流长度一致，设备按 `param` 取字节，忽略最后一个 250 B 包的补零。

### 6.6 格式自校验（已用抓包回放验证）

把 `param=9388 / seq=0 / posLen=0` 那条 0xCC 的 9,388 B 原始流取出，去掉末尾 `EF 69` 后按 §6.4 解析：

```
delta 长度 9386 → 525 条记录 / 2349 像素 / 消耗 9386 B（精确闭合）
另一帧 param=6823 → 390 条记录 / 1689 像素 / 6821 B（精确闭合）
视频帧 param=19903 posLen=10995 → delta 8906 B（506 条记录）精确闭合，随后 posInfo 10995 B（2199 条记录）精确闭合
空帧  param=11208 posLen=11200 → delta 6 B = 80 00 00 00 00 00，posInfo 11200 B 精确闭合
```

四种情形全部**逐字节闭合**，证明 `delta || posInfo || EF69` 布局与 3 字节像素记录格式正确。

---

## 7. 0xD0 位置信息（posInfo）

### 7.1 何时发

`ShowFullImage(data, len, isDiiferInite)`：

```
if (!isDiiferInite) { send 0xC8(param=data.Length); send raw(data); return; }
else                { send 0xCA(param=data.Length); send raw(data); SetPngPosInfo(data); }
```

- `isDiiferInite = (currentTheme.videoPath != null && !_28BB())` —— 播视频时才 true。
- `SetPngPosInfo` 内部：`pos = 깬.ꔂ(img, w, h, out len)`；若 `len > 0` 则 `pos[len]=0xEF; pos[len+1]=0x69; len+=2;` 再
  `0xD0(param=len, payload=null)` + 原始流 `pos||EF69`。
- **只跟在 0xCA 之后**，每个整帧一次；0xCC 之前不需要单独发 0xD0。

### 7.2 长度关系（实测）

`0xD0` 的 posInfo 由 **`ShowFullImage` 收到的整帧 `data`** 计算；
`0xCC` 头里的 `posLen` 由 **`ShowSeqImage` 的 `img_seq`（差分基准帧）** 计算。
两者输入不同，**长度只在图像内容相同时才巧合相等**：

| 会话 | 0xD0.param | 其后第一个 0xCC.posLen | 关系 |
|------|-----------|------------------------|------|
| 21:27 | 10997 | 10995 (=param-2) | 巧合相等（同一帧做基准） |
| 21:54 | 11007 | 11200 | **不等**（posLen 更大） |
| 21:56 | 11047 | 10980 | **不等** |
| 22:04 | 10947 | 10945 (=param-2) | 巧合相等 |

> **必须记住的不对称**：0xD0 的 param 把 EF69 算进去（`SetPngPosInfo` 里先 `A_ += 2` 再发），
> 而 0xCC 头里的 `posLen` 只算 posInfo 本身、不含 EF69。复刻时必须照做。
>
> 同时也说明：**0xCC 的 posLen 不能从 0xD0 推算**，必须自己对基准帧跑 posInfo 编码器（或干脆 `NeedPosInfo=false`）。

### 7.3 posInfo 内容（结构已实测，语义仅部分确认）

旧编码器 `깬.ꔂ(byte[] img, int w, int h, out int len)` 产出 **5 字节定长记录流**：

```
[pos:3 BE][value:2 BE]   × N ，总计 len = 5N
pos ≈ 起始像素下标 (y*w + x)，整体单调递增
value 在前若干行呈"每行 2 段、段长逐行缓变"（95,95,103,103,107,107…），像"该行不透明游程长度"
最后 1~2 条记录是收尾/哨兵（实测 (168480, 61920) 后跟 (0,0)）
```

实测（21:27 第 1 帧，posInfo 10,995 B）：2,199 条记录，5 字节对齐**完整消耗**；delta 段同样精确消耗 8,906 B。
设备据此知道"哪些像素是内容"（alpha > 15 的区域），用于与设备侧背景/视频合成。

> 语义细节（尤其末条哨兵）不影响静态主题——静态模式 `NeedPosInfo=false`，完全不产生 posInfo。
> 若将来要做视频叠加，posInfo 编码器的逐字节还原见同目录 **`encoder.md` §6**。

### 7.4 静态主题需要 posInfo 吗？

**不需要**。官方静态主题 `NeedPosInfo=false`：全程没有 0xD0，所有 0xCC 的 `posLen=0`。
全日志 2,215 个 0xCC 中只有 298 个 `posLen≠0`，全部落在 4 个视频会话（21:27 / 21:54 / 21:56 / 22:04）。

---

## 8. needReSend / renderCnt

### 8.1 查询与解析（`뽞.QuerryResend` → 状态机 `ꪨ`）

```csharp
눰._A9B7(207, 1, null, 0);                     // 0xCF
var s = await 눈._A9B7(1, A_1:false, 1);       // 读：1 次重试 / 1000 ms 超时
if (!s.Contains(":")) {                        // 无冒号（空应答 / 旧 ack）
    curVideoCnt++; revNullCnt++;
    return revNullCnt > 3 ? false : true;      // 连续 4 次才判失败
}
var p = s.Split('|');
int.TryParse(p[1].Replace("renderCnt:", ""), out renderCnt);
if (p.Length > 2) themeIndex = p[2].Replace("theme:", "");
lastVideoCnt = curVideoCnt;
curVideoCnt  = renderCnt;
return !p[0].Contains("needReSend:1");         // true=正常, false=要重发
```

应答格式：`needReSend:0|renderCnt:3383` 或 `needReSend:0|renderCnt:44|theme:xxx`。

### 8.2 needReSend:1 的实际动作

`Monitor.목`：

```csharp
res = await lcdDev.QuerryResend();
if (!res) { reSendInite = true; reStartVideoMark = true; reSendCnt++; }
```

下一轮 `RenderFrame` 开头 `if (reSendInite) → 整帧重载路径`：

1. `renderFirstCnt++`，>5 则打印 `renderFirstCnt err` 并抛异常（外层 `ERRCNT++` + 重连）；
2. 丢 0x2C 哑包 → 设亮度 → **0xC8/0xCA 整帧 ×2**；
3. `imgSeqIndex = 0`，把当前画面作为新基准入队；
4. `reSendInite = false`，继续正常的 0xCF/0xCC 循环。

**所以是"整帧重来"，不是"重发同一增量"，也不是"发下一帧"。**

### 8.3 renderCnt 的作用

- 设备侧渲染计数器：静态主题恒定（本机 **3383**，5 分钟不变）；视频模式每秒 +19~30。
- 客户端用它做**卡顿检测**：`lastVideoCnt == curVideoCnt` → 视频没在推进 → `reSendInite=true` 且当帧返回 false。
- 它**不**参与"是否需要重发"的判定（那只看 `needReSend:`），也**不**用于 seq。

### 8.4 重要：needReSend 有 1 帧延迟

- 客户端每轮只 `Read(1024)` **一块**应答；设备每轮产生一块。
- 首帧后缓冲区里有两块 `full_png_sucess`，前两块分别被"整帧后 read"和"第一轮 0xCF 后 read"吃掉。
- 此后每一轮读到的都是**上一轮 0xCF 的应答**（时间戳与下一次 0xCF 同毫秒即为证据）。
- 结论：`needReSend:1` 会在**下一帧**才被处理。自研服务若想更快恢复，可把应答读取改为独立读线程 + 队列，不必保留这个延迟。

### 8.5 抓包里从未出现 needReSend:1

全日志 `grep "needReSend:1"` = **0 次**（2,295 个应答全是 `needReSend:0`）。
所以"重发"分支只有代码证据，无实测样本 —— 首版服务建议：**保留"needReSend=1 → 整帧重载 + seq 归零"的行为**，并加日志观察真实触发率。

---

## 9. 抓包实证

### 9.1 22:31 会话（静态主题，首个 C8）

| 时间 | 方向 | 报文 | 说明 |
|------|------|------|------|
| 22:31:00.287 | → | `01 EF69 00000001 00 0000 C5D3` | 握手 |
| 22:31:00.330 | ← | `chs_34inch.dev1_rom1.88` | rom=1.88 |
| 22:31:00.333 | → | `79 EF69 00000001 00 0000` | StopVideoPlay |
| 22:31:00.542 | → | `96 EF69 00000001 00 0000` | media stop 查询 |
| 22:31:00.574 | ← | `media_stop` | |
| 22:31:00.635 | → | `7B EF69 00000001 00 0000 AA` | SetBrightness(0xAA) |
| 22:31:00.704 | → | `7D EF69 00000005 00 0000 AA 00 00 00 00` | SetConfig |
| 22:31:00.742 | → | `86 EF69 00000001 00 0000` | SetMonitorStart |
| 22:31:00.863 | → | 250 × `2C` | 哑包（꽝） |
| 22:31:00.883 | → | `7B … 01 … AA` | 重设亮度 |
| 22:31:00.992 | → | `C8 EF69 000E1000 00 0000` + 3702 包(921,798 B) | 整帧 #1 |
| 22:31:01.564 | | | 整帧 #1 传完（572 ms） |
| 22:31:01.670 | → | `C8 … 000E1000 …` + 3702 包 | 整帧 #2 |
| 22:31:02.245 | | | 整帧 #2 传完 |
| 22:31:02.260 | ← | `full_png_sucess` | ack #1 |
| 22:31:02.449 | → | `CF EF69 00000001 00 0000` | 第 1 次查询 |
| 22:31:02.449 | ← | `full_png_sucess` | ack #2（被本轮读走） |
| 22:31:02.454 | → | `CC EF69 000024AC 00 0000` `00000000 00000000` + 38 包 | **seq=0, posLen=0, param=9388** |
| 22:31:03.674 | → | `CF …` | 第 2 次查询 |
| 22:31:03.674 | ← | `needReSend:0|renderCnt:3383` | 上一轮 0xCF 的应答 |
| 22:31:03.676 | → | `CC EF69 00001F48 …` `00000001 00000000` + 33 包 | seq=1, param=8008 |
| 22:31:04.462 | →/← | `CF` / `needReSend:0|renderCnt:3383` | |
| 22:31:04.466 | → | `CC … 00000008 …` `00000002 00000000` + 1 包 | **seq=2 空帧 param=8** |
| 22:31:05.475 | → | `CC param=6823` `00000003` | seq=3 |
| 22:31:06.481 | → | `CC param=6023` `00000004` | seq=4 |
| … | | | 每秒一帧，直到 22:36:16 seq=311 |

会话结束前无关闭命令（进程退出即断链）。

### 9.2 22:37 会话（同一模板，验证可复现性）

| 时间 | 报文 |
|------|------|
| 22:37:18.458 | `01` + `C5D3` → 18.510 `chs_34inch.dev1_rom1.88` |
| 22:37:18.513 | `79`(StopVideo) |
| 22:37:18.724 | `96` → 18.755 `media_stop` |
| 22:37:18.816 | `7B` \[AA\] |
| 22:37:18.887 | `7D`(5) \[AA 00 00 00 00\] |
| 22:37:18.924 | `86`(SetMonitorStart) |
| 22:37:19.057 | 250 × `2C` |
| 22:37:19.079 | `7B` \[AA\] |
| 22:37:19.190 | `C8` + 3702 包 |
| 22:37:19.872 | `C8` + 3702 包 |
| 22:37:20.465 | ← `full_png_sucess` |
| 22:37:20.625 | `CF` |
| 22:37:20.626 | ← `full_png_sucess` |
| 22:37:20.630 | `CC param=8171 seq=0 posLen=0` + 33 包 |
| 22:37:21.848 | `CF` / ← `needReSend:0|renderCnt:3383` |
| 22:37:21.851 | `CC param=5124 seq=1` + 21 包 |
| 22:37:22.638 | `CF` / ← 应答 |
| 22:37:22.641 | `CC param=8 seq=2` + 1 包 |
| 22:37:23.645 | `CC param=8874 seq=3` + 36 包 |
| 22:37:24.653 | `CC param=6140 seq=4` + 25 包 |

两个会话（相隔 6 分钟、两次独立连接）报文序列**逐条一致**，仅图像内容/长度不同 → 模板可靠。

### 9.3 差分/视频模式对照（21:27:32）

```
0x86 → 0x7B → 0xCA(921600)+3702包 → 0xD0(10997)+45包
            → 0xCA(921600)+3702包 → 0xD0(10997)+45包
            → ← seq_png_init_sucess ×2
            → 0xCF → ← seq_png_init_sucess
            → 0xCC param=19903 seq=0 posLen=10995 + 80包
            → 0xCF → ← needReSend:0|renderCnt:44
            → 0xCC param=30722 seq=1 posLen=11200 + 124包 …
```

`0xD0` 与 `0xCA` 一一对应（每个整帧一次）。本会话 `0xCC.posLen(10995) = 0xD0.param(10997) - 2` 只是**巧合**
（两者输入是同一张图）；换成 21:54 会话就对不上（11007 vs 11200），见 §7.2。

---

## 10. 与任务给定事实的修正

| 给定 | 实际 | 依据 |
|------|------|------|
| "flag, **8 字节保留**, payload" | **2 字节保留**（offset 8..9），payload 从 **10** 开始，容量 240 | 4,644 个命令包 byte[8..9]=0；`_A9B7` 里 `Array.Copy(payload,0,array,10,len)` |
| "115200 8N1" | 只是名义值；实测 ≈1.6 MB/s | 921,600 B / 572 ms |
| "0xC8 每个串口连接只提交一次" | 官方**每次整帧都发两遍**，且同一连接内可多次整帧（重发时） | 22:31:00.992 + 01.670 |
| "0xCC param = 增量帧长度（约 6–50 KB）" | 正确，但 **param 还包含 EF69 尾（+2）**，最小 8（空帧） | 全部 2,215 条 CC |
| "0xD0 param = 位置信息长度" | 是 **posInfoLen + 2**（含 EF69）；与后续 0xCC 的 `posLen` **无固定关系** | §7.2 四个视频会话对比（21:54：11007 vs 11200） |
| "官方静态主题：0x96, 0x86, 0xC8×2" | 中间还有 `0x79、0x7B、0x7D、0x2C 哑包、0x7B` | §9.1 |
| "0xCA 是关键帧" | 0xCA 只在**差分/视频模式**用，静态主题不用 | 8 个 0xCA 全在 4 个视频会话 |
| "需要每帧重连串口" | 不需要；一条连接可连续跑 312 帧 / 5 分钟以上 | 22:31–22:36 |

---

## 11. 最小可复现发送序列（伪代码）

```csharp
// ── 0. 打开串口：115200 8N1（值无所谓，CDC 忽略）；不要每帧重开
var port = new SerialPort("COM9", 115200, Parity.None, 8, StopBits.One);
port.ReadTimeout = 100; port.WriteTimeout = 5000; port.Open();

// ── 1. 链路层
void SendCmd(byte cmd, uint param, byte flag = 0, byte[] payload = null)
{
    var p = new byte[250];
    p[0] = cmd; p[1] = 0xEF; p[2] = 0x69;
    p[3] = (byte)(param >> 24); p[4] = (byte)(param >> 16);
    p[5] = (byte)(param >> 8);  p[6] = (byte)param;
    p[7] = flag;                 // p[8]=p[9]=0
    if (payload != null) Array.Copy(payload, 0, p, 10, payload.Length);
    port.Write(p, 0, 250);
}

void SendRaw(byte[] data, int len)         // 249 + 1
{
    var p = new byte[250];
    for (int off = 0; off < len; off += 249)
    {
        Array.Clear(p, 0, 250);
        Array.Copy(data, off, p, 0, Math.Min(249, len - off));
        port.Write(p, 0, 250);
        if ((off + 249) % 24900 == 0) Thread.Sleep(1);
    }
}

string ReadResp(int timeoutMs = 1000)      // 一次读一块（通常 1024 B，NUL 填充）
{
    var buf = new byte[1024];
    int n;
    try { n = port.Read(buf, 0, buf.Length); }   // ReadTimeout=100 → 超时抛 TimeoutException
    catch (TimeoutException) { return ""; }
    int z = Array.IndexOf(buf, (byte)0, 0, n);
    return Encoding.UTF8.GetString(buf, 0, z < 0 ? n : z);
}

// ── 2. 建链
SendCmd(0x01, 1, 0, new byte[]{0xC5, 0xD3});
var info = ReadResp();                     // "chs_34inch.dev1_rom1.88"
SendCmd(0x79, 1);                          // 停视频
SendCmd(0x96, 1); ReadResp();              // 等 "media_stop"（可重试 ≤20 次）
SendCmd(0x7B, 1, 0, new byte[]{ brightness });          // 0xAA
SendCmd(0x7D, 5, 0, new byte[]{ brightness, 0, 0, 0, 0 });
SendCmd(0x86, 1);                          // SetMonitorStart
Thread.Sleep(100);

// ── 3. 整帧（关键帧）—— 发两遍
void SendFullFrame(byte[] bgra480x480r90, bool differMode /*视频才 true*/)
{
    SendCmd(differMode ? (byte)0xCA : (byte)0xC8, 921600);
    SendRaw(bgra480x480r90, 921600);       // 无 EF69 尾
    if (differMode) {
        var pos = BuildPosInfo(bgra480x480r90, 480, 480, out int plen);
        var buf = new byte[plen + 2];
        Array.Copy(pos, buf, plen); buf[plen] = 0xEF; buf[plen+1] = 0x69;
        SendCmd(0xD0, (uint)(plen + 2));
        SendRaw(buf, plen + 2);
    }
}
SendRaw(Enumerable.Repeat((byte)0x2C, 250).ToArray(), 250);   // 哑包（꽝）：250×0x2C
SendFullFrame(frame0, false); Thread.Sleep(100);
SendFullFrame(frame0, false);              // 官方固定两遍
DrainAcks();                               // 把 "full_png_sucess" 读掉（读到 100 ms 超时为止）

// ── 4. 稳态
long seq = 0;
byte[] baseFrame = frame0;
while (running)
{
    SendCmd(0xCF, 1);
    var status = ReadResp(1000);           // "needReSend:0|renderCnt:3383"
    if (status.Contains("needReSend:1"))   // ★ 整帧重载
    {
        SendFullFrame(current, false); Thread.Sleep(100);
        SendFullFrame(current, false);
        DrainAcks(); seq = 0; baseFrame = current; continue;
    }
    if (!status.Contains("needReSend:")) { /* 旧 ack / 空应答：忽略，照常发帧 */ }

    var delta = EncodeDelta(baseFrame, current);        // §6.4；空则 6 字节 0x80 标记
    int posLen = 0;
    var raw = new byte[delta.Length + 2];
    Array.Copy(delta, raw, delta.Length);
    raw[^2] = 0xEF; raw[^1] = 0x69;                     // 尾部标记，计入 param

    var hdr = new byte[8];
    hdr[0]=(byte)(seq>>24); hdr[1]=(byte)(seq>>16); hdr[2]=(byte)(seq>>8); hdr[3]=(byte)seq;
    hdr[4]=(byte)(posLen>>24); hdr[5]=(byte)(posLen>>16); hdr[6]=(byte)(posLen>>8); hdr[7]=(byte)posLen;  // 静态=0

    SendCmd(0xCC, (uint)raw.Length, 0, hdr);            // param = delta + posInfo + 2
    SendRaw(raw, raw.Length);

    baseFrame = current; seq++;
    WaitUntilNextTick(1000);                            // 官方 1 fps；可自行提速
}
```

### 关键实现提示

1. **整帧是关键帧**：每次建链必须先发 `0xC8`/`0xCA` 921,600 B（顺时针 90° 的 BGRA，480×480×4），官方发两遍；之后才能发 0xCC。
2. **不要每帧 `close/open`**：一条连接可跑几十分钟（实测 312 帧 / 5 分钟以上）。
3. **空帧也要发**（`param=8`），否则 seq 与设备不同步。
4. **`param` 一定含 `EF 69` 尾（+2）**，`posLen` 不含。
5. **`needReSend` 用整帧恢复**，并把 seq 归零。
6. **提速**：官方 1 fps 只是产品策略，协议本身没有节流。若追求实时，可先试 10–30 fps；但要盯 `needReSend`，一旦为 1 就整帧重载（并考虑退避）。
7. **应答读取**建议独立线程 + 队列，避免官方那 1 块/帧的延迟（见 §8.4）。

---

## 12. 未验证项 / 风险

| 项 | 说明 | 建议 |
|----|------|------|
| `needReSend:1` 的真实触发条件 | 日志中从未出现 | 代码行为已确认（整帧重载 + seq 归零）；实测时加日志 |
| 设备是否校验 `seq` 连续性 | 未知 | 先用严格 +1；若设备不校验，重连后可继续编号 |
| 发送速率上限 | 官方 1 fps，未压测 | 逐步提高帧率，观察 `needReSend` 与单帧耗时 |
| `0xCC` 与 `0xCF` 是否必须成对 | 官方每帧成对；未测"只发 CC 不查询" | 首版照抄成对；稳定后再省 0xCF |
| 整帧是否必须发两遍 | 未测单遍 | 先照抄两遍（成本 0.6 s×2） |
| 0x2C 哑包作用 | 推测为唤醒/冲刷 | 保留，成本 250 B |
| posInfo 精确语义 | 已确认 5 B 游程表、alpha>15 | 静态主题不需要，暂不实现 |
| 65000 游程溢出 | 编码器返回 null → 静默丢帧 | 自研编码器应主动分段，避免依赖该退化路径 |

---

## 附录 A：日志解析脚本要点（可复现）

1. 按行读，正则 `^(\d\d:\d\d:\d\d\.\d{3}) \[(\d+)\] (\S+)(.*)$`。
2. `W` 行：`W SerialPortStream 250 <hex> |ascii|`，`bytes.fromhex(hex)` 得 250 B。
3. 判定命令包：`b[1]==0xEF && b[2]==0x69`（原始数据包几乎不会撞上，且可结合前一条命令的 `param` 消费计数二次校验）。
4. 维护"待消费原始字节数"：命令 `param` 入账，每个数据包减 249。
5. `RA` 行：`RA <len> <hex> |ascii|`，取第一个 `0x00` 前的 UTF8。

## 附录 B：关键常量

```
m_ꔂ = 249        每包数据字节
m_ꗳ = 250        每包线上字节
0xEF69 = 61289   同步字 / 数据流结束标记
921600 = 0x000E1000   480×480×4
空增量 payload = 80 00 00 00 00 00 EF 69
命令头 = cmd(1) + EF69(2) + param(4) + flag(1) + 00 00(2) = 10 B
```
