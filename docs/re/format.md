# SAMA 3.4" 水冷屏 0xCC 私有增量图像编码 — 抓包逆向报告

> 目标：从官方串口抓包反推 `0xCC` 增量数据的真实格式，判断与 `SamaEncoder.exe` 是否一致，并给出新 C# 服务构造合法 CC 帧的具体做法。
>
> 范围：只做离线分析。未启动 SAMA GUI，未打开/写入 COM9，未修改任何现有源码。`SamaEncoder.exe` 仅作为“官方编码器 oracle”被调用（它通过反射加载 `SAMA.exe`，不启动界面）。
>
> 证据：`spy\spy_serial.log`（205 MB，485,527 行，流式解析）、`decompiled\`、`SamaEncoder\`，以及本目录下新生成的 `official_cc_*.bin` / PNG。
>
> 工作区已有一份 `encoder.md`（745 行）对反编译代码做了逐行还原；本报告补充**抓包时序、CC 载荷分层、图像级验证、与 helper 的逐字节对比**，并给出可直接照做的组包流程。两者结论一致。

---

## 0. 结论速览（TL;DR）

1. **0xCC 不是原始 BGRA/BGR888/RGB565，也不是通用 RLE/位平面**，而是“**稀疏差分补丁表 + 每像素 3 字节**”：
   - 每个补丁 = `[24-bit 起始像素线性下标][16-bit 连续长度]` + `3 字节 × 长度`；
   - 长度为 1 的补丁把长度字段省掉，改成把下标最高位置 1（首字节 `0x80..0x83`），共 6 字节；
   - 像素 3 字节是 **B6/G6/R8/A4** 打包，不是 BGRA 截断：
     `B'=(B&0xFC)|((A>>6)&3)`，`G'=(G&0xFC)|((A>>4)&3)`，`R'=R`。
2. **CC 的 `param` 不是纯 delta 长度**，而是：
   ```
   payload = delta(N 字节) ‖ pos(P 字节，可空) ‖ EF 69
   param   = N + P + 2
   ```
   8 字节应用头 `[seq:4B BE][posLen:4B BE]` 在**命令包**的第 10–17 字节，**不计入 param，也不在 249 字节数据流里**。
3. **静态主题**（官方 `0xC8` 全帧）的 `posLen` 恒为 0，全帧 alpha 全 `0xFF`；`0xCC` 只发 `delta ‖ EF 69`。**视频/透明主题**（官方 `0xCA`）才带 pos。
4. **官方每个连接只在启动时发一次 `0xC8` 全帧（连发两遍，内容完全相同），之后一直用 `0xCC` 增量**。抓包中最长一次连接连续发了 **510 帧 / 8 分 35 秒**，没有重连、没有再发 `0xC8`。
5. **`SamaEncoder.exe` 的编码结果与官方 CC 逐字节一致——但前提是按 `(previous, current)` 传参**。它的 `Program.cs` 用法说明写成了 `<current.bgra> <previous.bgra>`，参数顺序是反的；按说明调用会把上一帧的颜色编进去，画面永远滞后一帧。
6. 用反推的格式解码官方 CC 得到完整的 480×480 图像：红色 CPU/GPU 监控主题，数字/百分比/内存值每秒变化；解码结果与官方全帧一致，差异像素数恰好等于 delta 里的像素数。

---

## 1. 抓包与序列提取

### 1.1 日志规模与解析方法

`spy_serial.log` 每行格式：

```
21:27:29.450 [13] W SerialPortStream 250 <500 hex chars> |ascii|
21:27:29.452 [13] W-ok
21:27:29.495 [13] RA 24 <hex> |chs_34inch.dev1_rom1.88.|
```

- `W` = 官方写入串口的 250 字节包；`RA` = 设备返回。
- 全日志共 **241,616 个 W 包**、2,295 条 RA。
- 命令统计：`0xCC` 2,215、`0xCF` 2,215、`0xC8` 30、`0xCA` 8、`0xD0` 8、`0x96` 19、`0x86` 19、`0x7B` 38、`0x7D` 19、`0x01` 29、`0x79` 19、`0x6E` 8、`0x78` 4、`0x82` 4、`0x87` 3。
- 解析器按“命令包 + 其后 `ceil(param/249)` 个原始包”的状态机流式重组；2215 个 CC 全部重组成功，无 short read。

### 1.2 选定的“完整静态主题序列”

官方在 **22:07:00.363 – 22:08:46.536** 建立了一次静态主题会话：

| 时间 | 命令 | param | 说明 |
|---|---|---|---|
| 22:07:00.363 | `0x01` | 1 | 查询设备，payload `C5 D3 00...`；返回 `chs_34inch.dev1_rom1.88` |
| 22:07:00.425 | `0x79` | 1 | StopVideoPlay |
| 22:07:00.640 | `0x96` | 1 | StopMedia；返回 `media_stop` |
| 22:07:00.748 | `0x7B` | 1 | SetBrightness，payload `AA` |
| 22:07:00.810 | `0x7D` | 5 | 显示配置，payload `AA 00 00 00 00` |
| 22:07:00.842 | `0x86` | 1 | SetMonitorStart |
| 22:07:00.963 | `0x7B` | 1 | SetBrightness，payload `AA` |
| **22:07:01.071** | **`0xC8`** | **921600** | 全帧 #1 |
| **22:07:01.756** | **`0xC8`** | **921600** | 全帧 #2（与 #1 完全相同） |
| 22:07:02.409 | `0xCF` | 1 | 提交/查询；返回 `full_png_sucess` |
| 22:07:02.412 | `0xCC` | 14730 | seq=0, posLen=0, delta=14728 |
| 22:07:03.410 | `0xCF` | 1 | 返回 `needReSend:0\|renderCnt:3383` |
| 22:07:03.413 | `0xCC` | 37143 | seq=1, posLen=0, delta=37141 |
| … | … | … | 每秒一帧，共 104 帧 |

- 两遍 `0xC8` 的 921600 字节内容 **SHA256 完全相同**：`1081140D9894C3BA2E8264A1B0D0B04D7C351FCFB74A015B8F18CC6305557622`。
- 这是官方“双写”冗余：`Monitor.cs:5461/5495` 连续调用 `ShowFullImage` 两次，代码里中间有 100 ms 延迟；抓包中第二遍在第一遍开始后约 0.68 s（第一遍 921600 字节传完）才开始。
- 该会话的 104 个 CC 全部 `posLen=0`，seq 从 0 连续到 103。
- 本报告取前 60 帧做详细分析，前 8 帧原始载荷保存为 `official_cc_00.bin … official_cc_07.bin`。

另外选了一次最长的静态会话 **23:09:39 – 23:18:14，510 帧**，用于证明“不重连连续刷新”可行；median 帧间隔 **1.012 s**（min 0.728，max 1.291），param median 9,984，max 18,033。

### 1.3 官方连接生命周期（为什么“每连接只发一次 C8”）

`spy\debug.log` 显示：每个 C8 会话之前都有 `new device!` / `连接成功` / `Start SN=`，即官方在切换主题/重新开始监控时**重新连接了设备**。每个连接内：

```
初始化(0x01/0x79/0x96/0x7B/0x7D/0x86/0x7B)
  → 0xC8 全帧 ×2
  → 0xCF
  → 0xCC 增量 × N（每秒一帧，每帧后 0xCF）
```

全日志 15 组 C8 会话，每组都是上述模式；**同一连接内没有再发过 C8**。这与“0xC8 每连接只提交一次”的已知现象吻合：C8 是“关键帧/进入序列模式”的初始化动作，不是每帧可重发的普通命令。

---

## 2. 250 字节传输层

### 2.1 命令包（250 字节定长）

来自 `됒.cs:2725`，与抓包完全一致：

```
偏移  长度  字段
0     1     cmd
1     2     EF 69（魔数）
3     4     param（大端 uint32）
7     1     flag
8     2     保留，恒 0
10    8     应用头（仅 CC 用）：seq(4B BE) + posLen(4B BE)
18    232   0
```

> 注意：应用头在 **+10**，不是 +8；+8/+9 是两个保留 0。已知资料里的“8 字节保留”应理解为“+8..+9 两个 0 + +10..+17 八字节应用头”。

静态会话第一帧 CC 命令包（22:07:02.412）实际字节：

```
0000  CC EF 69 00 00 39 8A 00 00 00 00 00 00 00 00 00
0010  00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

- `param = 0x0000398A = 14730`
- `flag = 0`
- `seq = 0`（+10..+13）
- `posLen = 0`（+14..+17）

视频会话第一帧 CC 命令包（21:27:34.213）：

```
0000  CC EF 69 00 00 4D BF 00 00 00 00 00 00 00 00 00
0010  2A F3 00 00 ...
```

- `param = 0x00004DBF = 19903`
- `seq = 0`
- `posLen = 0x00002AF3 = 10995`

### 2.2 原始数据流（249 + 1）

来自 `됒.cs:2855`：

- 每个数据包固定 250 字节，前 **249** 字节是有效数据，第 250 字节补 0；
- 最后一包不足 249 字节时，剩余补 0，仍然写满 250；
- 每 100 包（24,900 字节）`Thread.Sleep(1)`；
- 包数 = `ceil(param / 249)`，最后一包有效字节 = `param - 249*(包数-1)`。

原始数据流里**没有**命令头、没有 `EF69` 前缀；它就是 `payload` 的 249 字节分片。

### 2.3 全帧 `0xC8` / `0xCA`

- `param = 921600`（480×480×4），随后 3702 个数据包；
- 数据就是 480×480 BGRA，**没有** `EF69` 尾巴（抓包末尾是像素 alpha `FF`，不是 `EF 69`）；
- `0xC8` = 静态全帧（`isDiiferInite=false`）；`0xCA` = 视频/透明全帧（`isDiiferInite=true`），后者后面还会跟 `0xD0` 位置信息；
- 官方全帧连发两遍，内容相同。

### 2.4 `0xD0` 位置信息（仅视频/透明主题）

- `param = posLen + 2`；
- 数据 = `pos ‖ EF 69`；
- 抓包中 `D0 param=10997`，对应 `posLen=10995`；
- 静态主题不发 D0。

---

## 3. 0xCC 载荷布局（最关键的一层）

### 3.1 公式

```
CC payload = delta ‖ [pos] ‖ EF 69
param      = len(delta) + len(pos) + 2
header     = seq(4B BE) ‖ posLen(4B BE)     // 在命令包 +10，不在 payload 里
```

- 静态主题：`posLen = 0`，`param = len(delta) + 2`；
- 视频主题：`posLen ≈ 10,870..11,250`，`param = len(delta) + posLen + 2`。

### 3.2 两个真实例子

**静态**（22:07:02.412，seq=0）：

```
param=14730, posLen=0
→ deltaLen = 14730 - 0 - 2 = 14728
payload = <14728 字节 delta> ‖ EF 69
```

**视频**（21:27:34.213，seq=0）：

```
param=19903, posLen=10995
→ deltaLen = 19903 - 10995 - 2 = 8906
payload = <8906 字节 delta> ‖ <10995 字节 pos> ‖ EF 69
```

### 3.3 尾部校验

- 2215/2215 个 CC 的 `payload[param-2 : param]` 都等于 `EF 69`；
- 静态 60 帧的 delta 内部没有 `EF 69`；
- 全部 2215 帧的 payload 内部（去掉最后 2 字节）共出现 165 次 `EF 69`，分布在视频帧的 pos/delta 数据里，属于像素/区间数据的巧合（22.9 MB 中 165 次，低于随机期望）。**解析必须按 `param` 长度切，不能扫描 `EF69` 当结束符。**

---

## 4. Delta 编码格式（核心）

### 4.1 两种 run 记录

像素线性下标范围 `0 .. 230399`（480×480，row-major，紧打包 BGRA），24 位足够。

**A. 多像素 run（长度 N ≥ 2）**

```
+0  3B  起始像素下标，大端 24-bit（最高位必须为 0）
+3  2B  像素个数 N，大端 16-bit
+5  3B×N  像素数据
总长 = 5 + 3N
```

**B. 单像素 run（长度 N = 1）**

```
+0  3B  起始像素下标 | 0x800000（即首字节 bit7 置 1）
+3  3B  像素数据
总长 = 6
```

- 单像素标记是“首字节的最高位”，不是固定的 `0x80` 字节；因为下标 ≤ 0x0383FF，正常首字节只会是 `0x00..0x03`，单像素形态首字节为 `0x80..0x83`。
- 官方编码器只在“该像素前后都未变化”时产生单像素 run；若单像素 run 正好在帧末尾，某些路径可能仍写成 8 字节的普通 run（长度 1），解码器两种都要支持。
- run 按起始下标递增排列，互不重叠；未变化像素不写入，形成空洞。

### 4.2 3 字节像素打包（B6/G6/R8/A4）

输入内存序 **B,G,R,A**：

```
A4 = A >> 4                              // alpha 高 4 位
b0 = (B & 0xFC) | ((A4 >> 2) & 3)        // B 高 6 位 + A4 高 2 位
b1 = (G & 0xFC) | (A4 & 3)               // G 高 6 位 + A4 低 2 位
b2 = R                                   // R 全 8 位
```

解码：

```
B = b0 & 0xFC
G = b1 & 0xFC
R = b2
A = (((b0 & 3) << 2) | (b1 & 3)) << 4    // 只能恢复 alpha 高 4 位
```

- 丢的是 B/G 的低 2 位和 alpha 的低 4 位；从格式看，设备端按 18-bit 色 + alpha 高 4 位处理（推测）。
- 不要试图把 3 字节当 BGR888 直接显示：R 是对的，B/G 低 2 位被 alpha 占用。

### 4.3 无变化帧的兜底

编码器返回 0 时，官方**不**发空 payload，而是构造 6 字节：

```
80 00 00 00 00 00
```

它按格式看是“下标 0 的单像素 run，像素 00 00 00”；但官方把它当“无变化”哨兵使用。抓包中 511 个这样的帧，内容全部是这 6 字节。

> 复刻时直接照发这 6 字节即可。不要发 0 长度 payload（`param=2`），官方不这么做，设备行为未知。
>
> 解码/重建图像时应当把这一整串识别为 **no-op**；否则会按字面把 (0,0) 涂成透明黑。官方编码器的内部 prev 并没有因此改变——本报告对 49 个非空静态帧的重编码全部匹配，反证了这一点。

### 4.4 解码伪代码

```csharp
void ApplyDelta(byte[] frame, byte[] delta) {
    // 官方“无变化”哨兵：6 字节全零 + 首字节 0x80，按 no-op 处理
    if (delta.Length == 6 && delta[0] == 0x80 &&
        delta[1] == 0 && delta[2] == 0 && delta[3] == 0 &&
        delta[4] == 0 && delta[5] == 0) return;

    int i = 0;
    while (i < delta.Length) {
        int idx = (delta[i] << 16) | (delta[i+1] << 8) | delta[i+2];
        bool single = (idx & 0x800000) != 0;
        idx &= 0x7FFFFF;
        i += 3;
        int n;
        if (single) { n = 1; }
        else {
            n = (delta[i] << 8) | delta[i+1];
            i += 2;
        }
        for (int k = 0; k < n; k++) {
            byte b0 = delta[i], b1 = delta[i+1], b2 = delta[i+2];
            i += 3;
            int o = (idx + k) * 4;
            frame[o]   = (byte)(b0 & 0xFC);
            frame[o+1] = (byte)(b1 & 0xFC);
            frame[o+2] = b2;
            frame[o+3] = (byte)((((b0 & 3) << 2) | (b1 & 3)) << 4);
        }
    }
}
```

### 4.5 编码伪代码

```csharp
byte[] EncodeDelta(byte[] prev, byte[] cur, int maxRun = 65000) {
    var outp = new List<byte>(cur.Length);
    int n = cur.Length / 4;
    int i = 0;
    while (i < n) {
        if (prev[i*4]==cur[i*4] && prev[i*4+1]==cur[i*4+1] &&
            prev[i*4+2]==cur[i*4+2] && prev[i*4+3]==cur[i*4+3]) { i++; continue; }
        int start = i;
        i++;
        while (i < n && !PixelEqual(prev, cur, i) && (i - start) < maxRun) i++;
        int cnt = i - start;
        if (cnt == 1) {
            outp.Add((byte)(0x80 | ((start >> 16) & 0x7F)));
            outp.Add((byte)(start >> 8));
            outp.Add((byte)start);
            WritePixel(outp, cur, start);
        } else {
            outp.Add((byte)(start >> 16));
            outp.Add((byte)(start >> 8));
            outp.Add((byte)start);
            outp.Add((byte)(cnt >> 8));
            outp.Add((byte)cnt);
            for (int k = 0; k < cnt; k++) WritePixel(outp, cur, start + k);
        }
    }
    return outp.ToArray();
}

void WritePixel(List<byte> o, byte[] cur, int p) {
    int i = p * 4;
    byte B = cur[i], G = cur[i+1], R = cur[i+2], A = cur[i+3];
    byte a4 = (byte)(A >> 4);
    o.Add((byte)((B & 0xFC) | ((a4 >> 2) & 3)));
    o.Add((byte)((G & 0xFC) | (a4 & 3)));
    o.Add(R);
}
```

### 4.6 首 64 字节实例（静态 seq=0，delta 14728 字节）

`official_cc_00.delta` 开头：

```
01 74 3B 00 02 | 4F 4F D9 | 43 43 D6
01 76 03 00 04 | FF FF FF | FF FF FF | FF FF FF | 1B 1B CD
```

按 run 解析：

| # | 起始下标 | 长度 | 像素（解码后 B/G/R/A） |
|---|---|---|---|
| 1 | `0x01743B` = 95291 | 2 | `4F 4F D9` → B=4C,G=4C,R=D9,A=F0；`43 43 D6` → B=40,G=40,R=D6,A=F0 |
| 2 | `0x017603` = 95747 | 4 | 白 ×3（B=FC,G=FC,R=FF），再一个 `1B 1B CD` → B=18,G=18,R=CD |
| 3 | `0x01761A` = 95770 | 4 | `5F 5F DC`、白 ×2、`4F 4F D9` |

（上面的 A 是解码恢复的高 4 位；原始 alpha 多半是 `0xFF`，低 4 位在编码时被丢弃。）

视频 seq=0 的开头（`02 BD 84 00 02 ...`）同样可以直接按该格式解析：下标 `0x02BD84`=179588，长度 2，像素 `FF A7 A9` / `FF B7 B8`，随后是 `02 BF 63 00 03 ...`，再往后出现 `82 C1 43 ...` 的单像素形态（下标 `0x02C143`，首字节 `0x82` 的 bit7=1）。

### 4.7 统计与“标记”判断

对全部 2215 个 CC 帧：

| 项目 | 数值 |
|---|---|
| 非空 delta 帧 / 空帧 | 1,704 / 511 |
| delta 能精确解析到最后一个字节 | 1,704 / 1,704 |
| run 总数 | 1,092,675（单像素 260,761 = 23.9%，多像素 831,914） |
| run 长度 min / median / max | 1 / 3 / 60 |
| delta 长度 min / median / max | 6 / 8,936 / 44,193 |
| CC param min / median / max | 8 / 9,370 / 55,165 |
| posLen=0 的帧 | 1,917（全部 C8 静态帧） |
| posLen>0 的帧 | 298（全部 CA 视频帧），range 10,870–11,250 |
| 尾部 EF69 正确 | 2,215 / 2,215 |
| needReSend=1 | 0 / 2,199 条 `needReSend` 响应（本次抓包未出现） |

关于题目点名的几个字节：

- **`0x80`**：不是独立命令/帧头，而是“单像素 run”的首字节最高位。首字节落在 `0x80..0x83`；精确等于 `0x80` 只出现在下标 < 0x10000 的单像素 run，以及 6 字节空帧 `80 00 00 00 00 00`。
- **`0x7F` / `0xC9`**：没有结构含义。它们只是 delta/pos 数据里的普通字节（全 payload 中 `0x7F` 277,157 次、`0xC9` 12,133 次），频率由图像内容决定。`0xC9` 在命令枚举里是 201，但本版本官方从未在抓包中发送。
- **`0xEF69`**：只在 CC/D0 payload 的最后 2 字节作为尾部魔数；全帧 C8/CA **没有**。不要用扫描 `EF69` 的方式找结尾。

### 4.8 长度上限与官方编码器的边界行为

- run 长度字段是 16-bit，最大 65535；官方编码器把上限设为 **65000 像素**。
- 用 `SamaEncoder.exe` 做合成测试：
  - 连续变化 65000 像素 → 正常（`outlen=195007`，长度字段 65000）；
  - 连续变化 65001 像素且后面有未变化像素 → 编码器返回空（`frame encoder empty`）；
  - 整帧 230400 像素全变（run 一直到帧尾）→ **不报错，但长度字段截断为 `0x8400=33792`，编码器返回 691205 字节的 delta（helper 再加 `EF69` 后文件为 691207 字节），是坏帧**。
- 结论：新服务自己编码时，**每个 run 必须 ≤ 65000**，超过就切成多个 run；整帧全变不要用 CC，直接用 C8 关键帧。

---

## 5. 位置信息 pos（视频/透明主题，静态主题可忽略）

- 编码器 `깬.ꔂ(byte[] frame, int w, int h, out int len, int bpp=4, int max=65000)`（`깬.cs:5412`）。
- 格式：`[3B 起始下标][2B 长度]`，共 5 字节/条；只记录 **alpha > 15** 的连续像素区间，不存像素值。
- 末尾官方会多写一条 `00 00 00 00 00`（5 个 0）作为收尾/怪癖；这样 `posLen` 比实际区间表多 5。
- `D0` 包 = `pos ‖ EF 69`，`param = posLen + 2`。
- 静态 C8 全帧 alpha 全 `0xFF`，整个 230400 像素都是“不透明”，但官方**根本不发 pos**（`posLen=0`），说明设备对静态全帧不需要 alpha mask。
- 视频 CA 全帧 alpha 分布 0..255（0 有 144,920 个、255 有 75,630 个、>15 有 84,774 个），opaque 像素 84,774 个，pos 为 2,199 条 × 5 = 10,995 字节，和抓包 `D0 param=10997` 完全对上。
- 对 33 个非空视频帧，用“alpha>15 连续区间 + 5 个 0 收尾”重编码，33/33 与官方 pos 逐字节一致。

> 新服务做静态主题时 **不需要实现 pos**，`posLen` 固定 0 即可。只有将来要做透明/视频主题才需要。

---

## 6. 与 SamaEncoder 的对比

### 6.1 关键结论：helper 的实参顺序是反的

`SamaEncoder\Program.cs:55`：

```csharp
object[] fp = new object[] { current, previous, 0, 4, 0, 65000 };
frame = (byte[])frameMi.Invoke(null, fp);   // A_0=current, A_1=previous
```

官方调用（`뽞.cs:6950` / `Monitor.cs:5740`）：

```csharp
array2 = 깬.ꗳ(img, img_seq, 0, 4, out A_2);   // img=上一帧, img_seq=当前帧
ShowSeqImage(FrameQueue.Last().Value, currentFrameData, ...);
```

编码器输出里所有像素字节都取自 **`A_1`**（第二个参数）。所以：

- 正确用法：`SamaEncoder.exe <previous.bgra> <current.bgra> out.frame out.pos`
- 文档写法：`SamaEncoder.exe <current.bgra> <previous.bgra> ...` ← **会把上一帧颜色编进去**

合成单像素测试（previous 全黑，current 在 (0,0) 放 `B=0x12,G=0x34,R=0x56,A=0x78`）：

| 调用 | 输出（含 EF69） | 解释 |
|---|---|---|
| `helper prev cur` | `80 00 00 11 37 56 EF 69` | 正确：`B'=0x11,G'=0x37,R'=0x56` |
| `helper cur prev` | `80 00 00 00 00 00 EF 69` | 错：编码的是 previous 的黑色（恰好与空帧哨兵同形） |

### 6.2 与官方抓包逐字节对比

用抓包重建的上一帧/当前帧，按 `(previous, current)` 调 helper：

| 帧 | 官方 `official_cc_XX.bin` | helper 输出 | 结果 |
|---|---|---|---|
| 0 | 14,730 B, SHA `82b7a18e82960feb…` | 14,730 B, SHA 相同 | **完全一致** |
| 1 | 37,143 B, SHA `a1b7a412a2a77293…` | 37,143 B, SHA 相同 | **完全一致** |
| 2 | 33,394 B, SHA `a6f6035f8d989f71…` | 33,394 B, SHA 相同 | **完全一致** |
| 3 | 33,829 B, SHA `2372eebab7b46733…` | 33,829 B, SHA 相同 | **完全一致** |
| 4 | 16,664 B, SHA `46de86351f1d1bb3…` | 16,664 B, SHA 相同 | **完全一致** |

> 注意：因为静态帧 `posLen=0`，官方 `official_cc_XX.bin` 就是 `delta ‖ EF69`；helper 输出也是 `delta ‖ EF69`，所以两者**整文件**相同，不只是 delta 段相同。

反过来按文档顺序 `(current, previous)` 调 helper：输出长度碰巧也是 14,730，但从第 5 字节开始颜色就不对（`03 03 C8…` vs 官方 `4F 4F D9…`），证实颜色取的是 previous。

### 6.3 全量复现

- 用本报告第 4.5 节的 Python/C# 等价编码器，对静态会话 60 帧（含 11 个空帧，49 个非空帧）：**49/49 非空帧与官方 delta 逐字节一致**；空帧按哨兵处理。
- 对视频会话前 40 帧（含 7 个空帧，33 个非空帧）：**33/33 非空帧与官方 delta 逐字节一致**；pos 也是 33/33 一致。
- 说明：格式理解没有“碰巧对上”，而是可逆、可重编码的。

### 6.4 ROM 版本分支

- 抓包设备返回 `chs_34inch.dev1_rom1.88` → `romVersion=1.88 < 1.89` → 官方走 **3 字节/像素**的 `깬.ꗳ`（`깬.cs:5812`）。
- `base.m._28BB()` 为真且 `romVersion >= 1.89` 时走同族的 4 字节/像素变体 `깬.ꔂ(byte[], byte[], int, int, out int, int)`（`깬.cs:6134`）：run 头相同，但像素负载是完整 4 字节 BGRA，单像素 run 为 7 字节（3B 头 + 4B 像素）。本抓包设备不满足该条件，这条分支**未做抓包验证**。
- 新服务如果只针对本机这台 3.4"（ROM 1.88），实现 3 字节版即可；若要做通用，按 ROM 版本二选一。

---

## 7. 解码成 480×480 图像

把 `0xC8` 全帧作为初始 prev，依次应用 CC delta，得到完整帧；保存为 PNG：

- `re\c8_base.png` / `re\c8_base_r90.png`（C8 全帧）
- `re\official_cc_c8\frame_00.png` … `frame_11.png`
- `re\official_cc_c8\frame_00_r90.png` …（转正后的 UI）
- `re\c8_diff_00_01.png`（第 0→1 帧差异）

**方向**：原始 buffer 是 480×480 BGRA、row-major，但内容是 UI **顺时针旋转 90°** 后的结果。把原始 buffer **逆时针转 90°** 得到正常观看方向（PIL `rotate(90, expand=True)`；C# 用 `RotateFlip(RotateFlipType.Rotate270FlipNone)` 或等价的 CCW 旋转）。反过来说，新服务渲染出正常 UI 后，要**顺时针转 90°** 再写入 921600 字节 buffer。

**内容**：红色 CPU/GPU 监控主题：
- 左上 `CPU TEMP` + 大号 `50°`；
- 左侧四张卡片：CPU 9%、GPU 27%、CPU 50°、GPU 37°；
- 右侧渐变栏：VRAM 3797M、CPU 3864M、GPU 1462M、RAM 16489M；
- 中间有竖排 `SAMA TES` 水印。

**验证**：第 0→1 帧的差异像素数 = **10,191**，正好等于第 1 帧 delta 里记录的像素数（10,191），说明解码路径与官方编码路径完全对齐。差异图只出现在数字/百分比/内存值上，符合“每秒更新监控数据”的语义。

---

## 8. 新 C# 服务如何构造合法 CC 帧

### 8.1 连接与启动（每个连接一次）

```csharp
// 1) 打开 COM9: 115200 8N1, DtrEnable=true, RtsEnable=true
// 2) 可选握手/查询
Send(0x01, 1, new byte[]{0xC5,0xD3});      // 读回 chs_34inch.dev1_rom1.88
Send(0x79, 1, null);                         // StopVideoPlay
Send(0x96, 1, null);                         // StopMedia -> media_stop
Send(0x7B, 1, new byte[]{0xAA});             // SetBrightness
Send(0x7D, 5, new byte[]{0xAA,0,0,0,0});     // display config
Send(0x86, 1, null);                         // SetMonitorStart -> full_png_sucess
Send(0x7B, 1, new byte[]{0xAA});

// 3) 首帧全图，发两遍（内容相同）
byte[] raw = RotateCW90(renderUpright480x480BGRA()); // 921600, alpha=0xFF
SendFull(0xC8, raw);   // param=921600, 3702 包, 无 EF69
SendFull(0xC8, raw);   // 第二遍
ReadResponse();        // 期待 full_png_sucess

// 4) 提交/查询
Send(0xCF, 1, null);   // 期待 full_png_sucess

prev = raw;
seq = 0;
```

- 如果服务只显示静态主题，`0x7B/0x7D/0x86` 可照抄，保证设备进入 monitor/PNG 模式。
- `0x01` 的 payload 区前两字节固定为 `C5 D3`（其余为 0）；注意它的 `param=1` 并不表示 payload 长度，照抄这 2 字节即可。`0x7B` 的 `AA` 是亮度（官方配置值），可按需改。
- `0x86` 的响应可能是 `full_png_sucess`，也可能是 `seq_png_init_sucess`，取决于设备状态；不要写死只认一种。

### 8.2 每帧（不重连、不重发 C8）

```csharp
for (;;) {
    byte[] cur = RotateCW90(renderUpright480x480BGRA()); // 921600, alpha=0xFF
    byte[] delta = EncodeDelta(prev, cur, maxRun: 65000);
    if (delta.Length == 0) delta = new byte[]{0x80,0,0,0,0,0};

    byte[] payload = Concat(delta, new byte[]{0xEF,0x69});   // 静态 posLen=0
    SendCc(seq, payload);       // 见 8.3

    // 官方节奏：约 1 秒后提交/查询；可按需缩短，见 8.5
    Thread.Sleep(1000);
    string resp = SendCfAndRead();   // Send(0xCF,1,null)
    if (resp.Contains("needReSend:1")) {
        SendCc(seq, payload);        // 原样重发同一帧
        Thread.Sleep(1000);
        resp = SendCfAndRead();
    }

    prev = cur;
    seq++;
}
```

关键点：

- **不要每帧重连串口**；官方同一连接连续 510 帧。
- **不要每帧重发 0xC8**；C8 只在启动时发。
- `seq` 每连接从 0 开始，每帧 +1；抓包 19 个会话全部是 0,1,2,… 连续，无跳变。
- `prev` 必须是设备**当前已显示**的帧；如果上一帧发送失败/重发，`prev` 不要提前更新。
- `needReSend:1` 本次抓包没出现（0/2,199 条 `needReSend` 响应），但协议明确支持；重发时保持同一 `seq` 和同一 payload，然后再次 CF。

### 8.3 CC 命令包与数据包

```csharp
void SendCc(int seq, byte[] payload) {
    // 命令包
    byte[] cmd = new byte[250];
    cmd[0] = 0xCC;
    cmd[1] = 0xEF; cmd[2] = 0x69;
    WriteBE32(cmd, 3, payload.Length);       // param = delta + pos + 2
    cmd[7] = 0;                              // flag
    // cmd[8..9] = 0
    WriteBE32(cmd, 10, (uint)seq);           // seq
    WriteBE32(cmd, 14, 0);                   // posLen = 0（静态）
    port.Write(cmd, 0, 250);

    // 数据包：249 字节有效 + 1 字节 0
    int off = 0, packets = 0;
    while (off < payload.Length) {
        byte[] pkt = new byte[250];
        int n = Math.Min(249, payload.Length - off);
        Array.Copy(payload, off, pkt, 0, n);
        port.Write(pkt, 0, 250);
        off += n;
        if (++packets % 100 == 0) Thread.Sleep(1);
    }
}
```

**静态帧的 payload 就是 `delta ‖ EF69`**；不要把 `seq/posLen` 放进 payload，也不要给 `param` 加 8。

两个最小字节级例子：

```
# 0xC8 全帧命令（param=921600=0x000E1000，无应用头）
C8 EF 69 00 0E 10 00 00 00 00 00 00 00 00 00 00 ...

# 0xCC 空帧（seq=5, posLen=0, param=8）
CC EF 69 00 00 00 08 00 00 00 00 00 00 05 00 00 00 00 ...
随后 1 个数据包（前 8 字节有效，其余补 0）：
80 00 00 00 00 00 EF 69 00 00 ...

# 0xCF 提交/查询（param=1，无 payload）
CF EF 69 00 00 00 01 00 00 00 00 00 00 00 00 00 00 ...
```

### 8.4 关键帧 / 失步恢复

- 正常情况：一次 C8 之后永远用 delta 链，`prev` 与服务端渲染帧保持同步。
- 如果 `needReSend` 持续为 1、或画面明显花屏/失步：
  1. 先重发当前 CC（同 seq）；
  2. 仍失败则尝试 `0x96` → `0x86` → `C8 ×2` → `CF`，在同一连接上重新初始化（**未在官方抓包中验证**，官方遇到这种情况是重连设备）；
  3. 最稳妥：关闭并重新打开 COM9，重新走 8.1 的启动流程。官方每个主题会话本来就是重连的。
- 因为 C8 每连接只稳定提交一次，**不要把“每帧 C8”作为刷新手段**；这也正是新服务必须实现 delta 编码的原因。

### 8.5 刷新率

- 官方静态会话的帧间隔 median **1.012 s**（min 0.728，max 1.291），即 **~1 fps**；这是官方 UI 统计数据的更新节奏，不一定是设备渲染上限。
- USB CDC 的实际吞吐远高于 115200 标称值（抓包中 921600 字节全帧约 0.68 s 传完），10–40 KB 的 delta 传输只需几十毫秒。
- 建议新服务先按 **1 fps** 跑通，再逐步把 `Thread.Sleep(1000)` 降到 500/250/100 ms，观察：
  - `needReSend` 是否变 1；
  - `renderCnt` 是否还增长/是否卡住；
  - 画面是否出现撕裂、跳帧。
- 如果设备侧渲染是 1 fps 上限，就只能保持 1 fps；协议本身没有每帧 1 秒的强制字段，1 秒是官方应用的 pacing。
- **不要用 renderCnt 判断静态主题是否卡死**：22:07 静态会话的 103 条响应 renderCnt 全是 **3383**，104 帧都没变；而 21:27 视频会话的 renderCnt 会递增。静态主题只看 `needReSend` 和画面。

### 8.6 陷阱清单（逐条核对）

1. **参数顺序**：`EncodeDelta(previous, current)`，不是 `(current, previous)`。helper 的文档是反的。
2. **旋转**：buffer 是 UI 顺时针转 90°；正常 UI 要顺时针转 90° 再写入，或解码时逆时针转回。
3. **通道序**：B,G,R,A；像素 3 字节打包会丢 B/G 低 2 位、alpha 低 4 位。
4. **param 公式**：`delta + pos + 2`，包含 `EF69`，不包含 8 字节应用头。
5. **应用头位置**：命令包 +10..+17；+8..+9 是保留 0。
6. **249+1**：每包第 250 字节补 0，最后一包也补满 250。
7. **EF69 只加一次**：CC/D0 payload 末尾；C8/CA 全帧不加。
8. **静态 posLen=0**：不要画蛇添足发 pos；设备对全不透明静态帧不需要 mask。
9. **空帧哨兵**：`80 00 00 00 00 00`，不要发空 payload。
10. **run 长度 ≤ 65000**：超过要切；整帧全变用 C8，不要用 CC。
11. **单像素标记**：首字节 bit7，不是固定 `0x80`；解码时 `idx &= 0x7FFFFF`。
12. **不要扫描 EF69 当结尾**：按 `param` 切；视频 pos/delta 内部可能出现 `EF69`。
13. **seq 每连接从 0 开始**：不要全局自增不复位。
14. **prev 更新时机**：只有确认当前帧已被设备接受（CF 返回 needReSend:0）后才 `prev=cur`；否则重发同一帧。
15. **C8 每连接一次**：不要把 C8 放进每帧循环。

### 8.7 离线验证计划（不碰硬件）

1. 用 `official_cc_c8\frame_XX.bgra` 做 prev/cur，对 `official_cc_00.delta … 07.delta` 做单元测试：`EncodeDelta(prev, cur)` 必须逐字节等于官方 delta；再在末尾加 `EF 69`，必须等于 `official_cc_00.bin … 07.bin`。
2. 用 `official_c8_0.bin` 作为首帧，按官方 CC 序列逐帧解码，确认第 0→1 帧差异像素数 = 10,191。
3. 用 `SamaEncoder.exe prev cur` 作为第二 oracle，确认输出与官方 `official_cc_XX.bin` 整文件 SHA256 相同。
4. 上机时先只发 C8 ×2 + CF，确认能显示首帧；再逐帧重放官方 CC（带 CF），确认 60 帧都能刷新；最后切换成自己编码的 delta。

---

## 9. 证据文件（本目录）

| 文件 | 内容 |
|---|---|
| `official_cc_00.bin … 07.bin` | 静态 C8 会话前 8 帧的完整 CC payload（`delta ‖ EF69`，posLen=0） |
| `official_cc_00.delta … 07.delta` | 去掉 EF69 的 delta 段 |
| `official_cc_00.pos … 07.pos` | 静态帧 pos 段（均为 0 字节） |
| `official_cc_c8\frame_00.png …` | 解码后的 480×480 图像（原始方向） |
| `official_cc_c8\frame_00_r90.png …` | 逆时针转 90° 后的正常 UI |
| `official_cc_c8\frame_00.bgra …` | 解码后的 921600 字节帧（可直接作为下一帧的 prev） |
| `c8_base.png` / `c8_base_r90.png` | 官方 C8 全帧解码图 |
| `c8_diff_00_01.png` | 第 0→1 帧差异（数字/百分比变化） |
| `video_base_r90.png` / `video_cc0_r90.png` | 视频 CA 会话的解码图（透明主题） |
| `video_cc0_mask.png` | pos mask（alpha>15）可视化 |
| `official_c8_0.bin` / `official_ca_0.bin` | 提取的 921600 字节全帧 |
| `helper\helper_00.frame …` | `SamaEncoder.exe prev cur` 的输出，用于 SHA 对比 |
| `ccformat.py` | Python 参考编码/解码器 |
| `parse_log.py` | 流式日志重组器 |
| `encoder.md` | 反编译代码的逐行还原（已有，可配合阅读） |

---

## 10. 未决问题 / 上机建议

1. **1 fps 是设备上限还是官方 pacing？** 抓包只能看到官方 1 秒节奏；建议上机后从 1 fps 逐步提速，观察 `needReSend` 和画面。
2. **`needReSend=1` 的重发细节**：本次 2,199 条 `needReSend` 响应全部是 0，未观察到重发；建议在服务里保留最近 1–3 帧和 seq，按“同 seq 同 payload 重发 + 再 CF”实现。
3. **同连接中途重发 C8 是否可行**：官方每次换主题都重连，抓包中没有“同连接第二次 C8”；如果服务需要定期关键帧，优先测试 `0x96 → 0x86 → C8×2`，不行就重连。
4. **ROM ≥ 1.89 的 4 字节变体**：本机是 1.88，未抓包验证；如果新服务要兼容其他固件，需要再抓一次或按 `깬.cs:6134` 实现。
5. **长 run 的设备兼容性**：官方抓包最大 run 只有 60，合成编码器能出 65000；设备端是否接受长 run 未验证。保守起见可按 4096 或 8192 切分，代价是头部开销略增。
6. **0xC9 (201)**：命令枚举里有，但本版本官方从未发送；不要自行猜测其语义。

---

## 附：一句话回答题目

> 官方 0xCC 的“增量数据”= **稀疏差分补丁表**（24-bit 下标 + 16-bit 长度 + 3 字节 B6/G6/R8/A4 像素），后面可选跟 alpha 位置表，最后统一加 `EF 69`；静态主题只发补丁表 + `EF69`，`param = deltaLen + 2`。`SamaEncoder.exe` 在 `(previous, current)` 参数顺序下与官方输出逐字节一致；它文档里的 `(current, previous)` 是反的。新服务只需在连接建立时发一次 `0xC8` 全帧（官方连发两遍），之后每秒发一帧 `0xCC` + `0xCF`，即可在不重连、不启动官方 GUI 的情况下持续刷新。
