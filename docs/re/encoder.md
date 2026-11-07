# SAMA 3.4" 私有增量图像编码（0xCC）完整还原

> 目标文件：`E:\AIWorkSpace\decompiled\깬.cs`
> 目标方法：`public static byte[] ꗳ(byte[] A_0, byte[] A_1, int A_2, int A_3, out int A_4, int A_5 = 65000)` — **`깬.cs:5812`**
> 附属方法：`public static byte[] ꔂ(byte[] A_0, int A_1, int A_2, out int A_3, int A_4 = 4, int A_5 = 65000)` — **`깬.cs:5412`**（位置信息编码器）
> 同族方法：`ꔂ(byte[], byte[], int, int, out int, int)` — **`깬.cs:6134`**（4 字节/像素的变体，ROM ≥ 1.89 使用）
>
> 本报告只做静态分析与离线日志验证，未运行官方 GUI，未接触串口，未修改任何现有源码。

---

## 0. 结论速览（TL;DR）

1. `ꗳ` 是一个**逐行扫描的行程（run）编码器**：把「新帧」与「参考帧」逐像素比较，只有发生变化的像素才会被写进输出；连续变化的像素合并成一个 run，每个 run 只存**起始像素线性下标**，像素值按 **3 字节/像素**紧凑存储。
2. 输出缓冲区**没有任何全局头**，就是一连串 run 记录，从偏移 0 开始：
   - 普通 run：`[3B 大端起始下标][2B 大端像素数]` + `3B × N` 像素，共 `5 + 3N` 字节；
   - 单像素 run（只在前一像素未变化时才出现）：`[3B 大端下标 | 0x80][3B 像素]`，共 6 字节。
3. 3 字节像素格式不是 BGRA 截断，而是 **B6 + G6 + R8 + A4**：
   `B' = (B & 0xFC) | (A4 >> 2)`，`G' = (G & 0xFC) | (A4 & 3)`，`R' = R`，其中 `A4 = alpha >> 4`。
4. **`A_1` 是被发送（显示）的「新帧」，`A_0` 只是比较基准（参考帧）**。官方 `SamaEncoder.exe` 的实参顺序是反的（`current, previous`），所以它编码出来的是上一帧内容——本次已用真实数据 100% 证实（见 §8.3），新服务务必写成 `EncodeDelta(prev, cur, ...)`。
5. 魔数 `EF 69` **不由编码器写入**，它由调用方追加在整段 payload 末尾（`delta || pos || EF 69`）。
6. `A_2`（第 3 个参数）在方法体内**完全未被使用**，传 0 即可。
7. 位置信息编码器 `ꔂ` 是「alpha > 15 的像素区间表」，每条记录 5 字节且**没有像素数据**；`A_3`（out 参数）= 实际写入字节数。它有一个官方怪癖：若最后一像素仍不透明，收尾会多算 5 字节，导致尾部出现 **5 个 0 字节**（实测 298/298 个非空 pos 包全部如此）。

---

## 1. 方法定位与调用链

### 1.1 方法本体

| 项目 | 值 |
|---|---|
| 文件 | `E:\AIWorkSpace\decompiled\깬.cs` |
| 行号 | 5812 – 6132 |
| 签名 | `public static byte[] ꗳ(byte[] A_0, byte[] A_1, int A_2, int A_3, out int A_4, int A_5 = 65000)` |
| 返回 | 输出缓冲区（容量 = `A_0.Length`）；失败时返回 `null` 且 `A_4 = 0` |

同族还有 `ꔂ(byte[] A_0, byte[] A_1, int A_2, int A_3, out int A_4, int A_5 = 65000)`（`깬.cs:6134`），逻辑与 `ꗳ` 完全同构，区别只有两点：

* 像素负载是**完整 4 字节 BGRA**（不做 6/6/8/4 量化），单像素 run 压缩后是 **7 字节**（`5+4-2`）而不是 6 字节；
* 输出容量风险更大（见 §10.6）。

### 1.2 谁在调用（决定 A_0/A_1 语义）

`뽞.cs:6769` 的 `ShowSeqImage(byte[] img, byte[] img_seq, long seqNum, bool NeedPosInfo)`：

```csharp
// 뽞.cs:6814 / 6908 —— ROM 版本分支
num3 = (base.m.romVersion >= 1.89) ? 1 : 0;
...
// rom >= 1.89（뽞.cs:6828）
array2 = 깬.ꔂ(img, img_seq, 0, 4, out A_2);      // 4 字节/像素变体
// rom <  1.89（뽞.cs:6950）
array2 = 깬.ꗳ(img, img_seq, 0, 4, out A_2);      // ← 本报告主角，3 字节/像素
```

上层调用在 `UsbMonitorL\Monitor.cs:5740`：

```csharp
lcdDev.ShowSeqImage(FrameQueue.Last().Value, currentFrameData, ...);
```

抓包设备自报 `chs_34inch.dev1_rom1.88`（`spy_serial.log` 第 3 行），即 **romVersion = 1.88 < 1.89 → 走 3 字节的 `ꗳ`**。这与我们对 2215 个真实 0xCC 包 100% 成功按 3 字节格式解析完全吻合。

### 1.3 混淆结构如何还原

`ꗳ` 被 Dotfuscator 做了控制流平坦化，形态是：

```csharp
short num = 0;  int num2 = num;
switch (num2) { default: { ... while (true) { switch (num2) { case 4: ... num = 10; num2 = num; continue; ... } } }
                case 0: { /* 初始化 */ num = 28; num2 = num; goto default; } }
```

* 每个 `case N` 是一个基本块，块尾 `num = X; num2 = num; continue;` 就是 `goto X`；
* `case 0` 是唯一真正的入口（初始化）；
* `while(true) + switch` 是状态机主循环，`return` 出现在负责收尾的 case 里；
* 另有两处**不透明谓词**（opaque predicate）：

```csharp
num = -14122;
short num10 = num;
num = -14122;
switch (num10 == num) {            // 恒为 true
default:  /* ← 真正会执行的块 */   num = 9; num2 = num; continue;
case false:
case true: break;                  // ← 死代码
}
goto case 12;
```

ILSpy 把「恒真分支」渲染成了 `default:`，把不可达分支渲染成 `case false/true`。**按字面读 `goto case 12` 会让码流错位**——因为 `case 12` 是"写像素"块，不写 run 头。正确语义是走 `default:`（跳到 `case 9`「开新 run」）。该结论已被 2215 个真实包全部逐字节解析成功反证（§8）。

同样的不透明谓词还出现在主循环自增处（`case 1/28`）：字面读是 `goto case 8`，实际语义是 `num3++` 后回到循环判断。

---

## 2. 输入语义

| 参数 | 语义 | 实测取值 |
|---|---|---|
| `A_0` | **参考帧**（设备当前已显示的画面）的像素缓冲。只用于「相等比较」，其长度同时决定输出缓冲区容量 `new byte[A_0.Length]` | 921600 = 480×480×4 |
| `A_1` | **新帧**（要显示的画面）的像素缓冲。**输出中所有像素字节都取自它** | 921600 |
| `A_2` | **未使用**（方法体内零引用）。官方调用传 `0` | 0 |
| `A_3` | 每像素字节数（stride），即 BGRA=4。它不仅决定下标步长 `num3 * A_3`，还决定 alpha 分支：`A_3 == 4` 才读 alpha 半字节，否则一律当作不透明（`num8 = 15`） | 4 |
| `A_4` | `out`，返回时 = 实际写入字节数（`num7`）。失败时为 0 | — |
| `A_5` | 单个 run 的**像素数**上限（不是字节数），默认 65000。用于保证 2 字节长度字段不溢出；超限时方法返回 `null` | 65000 |

**像素格式**：内存序 BGRA（`+0=B, +1=G, +2=R, +3=A`），与 0xC8 全帧格式一致。
**宽高**：编码器**完全不关心**宽高/步长以外的几何信息——它只做**线性下标**扫描，所以"顺时针旋转 90°"是在调用方做的，编码器无感。
**行 stride**：只支持紧打包（`stride = width * bpp`），没有单独的 stride 参数。

> ⚠️ `A_1.Length` 决定扫描的像素数 `num12 = A_1.Length / A_3`；`A_0` 只被读，长度必须 ≥ 输出所需。

---

## 3. 输出缓冲区布局

### 3.1 结构总览

编码器输出 `byte[]`，**没有全局头、没有魔数、没有长度前缀**。它是从偏移 0 开始的一串记录：

```
+--------------------------------------------------------------+
| run #0 | run #1 | run #2 | ... | run #k                       |
+--------------------------------------------------------------+
  每条 run 两种形态之一：
  A) 多像素 run：  3B 起始下标(BE) | 2B 像素数(BE) | 3B*N 像素数据
  B) 单像素 run：  3B 起始下标(BE) | 3B 像素数据        (下标最高位置 1)
```

### 3.2 类型 A：多像素 run（N ≥ 2，或帧末尾未压缩的 N = 1）

| 偏移 | 长度 | 含义 |
|---|---|---|
| +0 | 1 | `(start >> 16) & 0x7F` —— 最高位恒 0 |
| +1 | 1 | `(start >> 8) & 0xFF` |
| +2 | 1 | `start & 0xFF` |
| +3 | 1 | `N >> 8` |
| +4 | 1 | `N & 0xFF` |
| +5 | 3N | 像素数据，见 §3.4 |

`start` = 该 run 首像素的**线性下标**（`y*width + x`，但编码器只当作一维下标）。
`N` = 该 run 覆盖的像素个数（大端 16 位，最大 65000）。
记录总长 = `5 + 3N`。

### 3.3 类型 B：单像素 run（压缩形态）

只有当 run 长度为 1 且**紧跟着遇到一个未变化像素**时才会产生：

| 偏移 | 长度 | 含义 |
|---|---|---|
| +0 | 1 | `((start >> 16) & 0x7F) | 0x80` —— **最高位 = 1，即"省略长度字段"标记** |
| +1 | 1 | `(start >> 8) & 0xFF` |
| +2 | 1 | `start & 0xFF` |
| +3 | 3 | 像素数据 |

记录总长 = **6 字节**（等价于把 5 字节头 + 3 字节负载整体前移 2 字节）。

> `0x80` 标记能成立的前提是像素总数 < 2^23 = 8,388,608（480×480 只有 230,400）。见 §10.5。
> **帧末尾不会做这种压缩**：若最后一个 run 只有 1 个像素且一直开到帧尾，它会以类型 A（`N=1`）的 8 字节形态写出。

### 3.4 3 字节负载（B6/G6/R8/A4）

```
byte0 = (B & 0xFC) | (A4 >> 2)
byte1 = (G & 0xFC) | (A4 & 3)
byte2 = R
其中 A4 = alpha >> 4（0..15）；当 A_3 != 4 时恒为 0x0F
```

* `B`、`G` 各丢低 2 位（量化到 6 bit），`R` 保留完整 8 bit；
* alpha 只保留高 4 位，拆成两份塞进 B、G 的低 2 位；
* `A4 = 0xF`（alpha=255）时 `byte0 |= 3, byte1 |= 3`；`A4 = 0`（alpha ≤ 15）时两位全 0。
* 解码端：`A4 = ((byte0 & 3) << 2) | (byte1 & 3)`。

### 3.5 魔数 `EF 69` 在哪里

**编码器不写它。** 调用方在拿到 `A_4` 长度后自行追加：

```csharp
byte[] frameOut = new byte[frameLen + 2];
Array.Copy(frame, frameOut, frameLen);
frameOut[frameLen] = 0xEF;
frameOut[frameLen + 1] = 0x69;   // 见 SamaEncoder\Program.cs:65-68
```

最终 0xCC 的 payload = `delta ‖ pos ‖ EF 69`，`param = deltaLen + posLen + 2`。

---

## 4. 完整伪代码（去混淆）

```
ꗳ(A_0 /*参考帧*/, A_1 /*新帧*/, A_2 /*未用*/, A_3 /*bpp*/, out A_4, A_5 = 65000):

    # ---- 等价于 case 0 初始化 ----
    pixCount = A_1.Length / A_3
    dst      = new byte[A_0.Length]
    w        = 0        # num7  写游标
    hdr      = 0        # num6  当前 run 的头偏移
    run      = 0        # num4  当前 run 的像素数
    open     = false    # flag2 是否有未闭合的 run
    runs     = 0        # num5  已闭合 run 数（只用于日志）
    emitted  = 0        # num11 已输出像素数（死变量，不影响输出）
    bppIs4   = (A_3 == 4)

    for i in 0 .. pixCount-1:                       # case 5/28/33/1
        p = i * A_3

        # ---- 判定该像素是否"变化" ----
        # 顺序：B → G → R → A（短路）
        if A_0[p]   != A_1[p]:   changed = true
        elif A_0[p+1] != A_1[p+1]: changed = true
        elif A_0[p+2] != A_1[p+2]: changed = true
        else: changed = (A_0[p+3] != A_1[p+3])      # case 3；A_3!=4 时同样会读 +3（见 §10.4）

        if not changed:                              # case 17 → 32
            if open:                                 # case 13
                if run == 1:                         # case 23 单像素压缩
                    dst[hdr] |= 0x80
                    dst[hdr+3] = dst[hdr+5]
                    dst[hdr+4] = dst[hdr+6]
                    dst[hdr+5] = dst[hdr+7]
                    w -= 2
                else:                                # case 26
                    dst[hdr+3] = run >> 8
                    dst[hdr+4] = run & 0xFF
                if run > A_5:                        # case 6/14 → 错误
                    log("...超出长度...", run)
                    A_4 = 0
                    return null
                open = false; run = 0; runs++        # case 14
            # 未变化的像素不写入任何字节（case 8/10）
            continue

        # ---- 变化像素 ----
        if not open:                                 # case 29 → 9
            dst[w]   = (i >> 16) & 0xFF
            dst[w+1] = (i >> 8)  & 0xFF
            dst[w+2] = i & 0xFF
            hdr = w
            w  += 5
            open = true

        A4 = bppIs4 ? (A_1[p+3] >> 4) : 0x0F         # case 20/21/24
        dst[w]   = (A_1[p]   & 0xFC) | (A4 >> 2)     # case 12/19
        dst[w+1] = (A_1[p+1] & 0xFC) | (A4 & 3)
        dst[w+2] =  A_1[p+2]
        w += 3
        emitted++
        run++

    # ---- 收尾（case 2/25/27）----
    if open:
        dst[hdr+3] = run >> 8                        # 不压缩、不检查 A_5
        dst[hdr+4] = run & 0xFF
    A_4 = w
    log("...run数...", runs)
    return dst
```

### 关键常量表

| 常量 | 出处 | 含义 |
|---|---|---|
| `0x80` | `깬.cs:6065` `array[num6] \|= 128` | 单像素 run 标记（下标最高位） |
| `0x7F` | 语义推导 | 下标高位掩码（解码时用） |
| `0xFC` | `깬.cs:6087-6088` | B/G 通道保留高 6 位 |
| `0x0F` / `15` | `깬.cs:5989` `num8 = 15` | `A_3 != 4` 时视作 alpha=255 |
| `>> 4` / `>> 2` / `& 3` | `깬.cs:6056, 6087-6088` | alpha 4 位拆分 |
| `65000` | 默认参数 `A_5` | 单 run 最大**像素数**（非字节） |
| `0xEF69` (= 61289) | `됒.cs:2795`、`뽞.cs:6857` | 包尾魔数（不在编码器内） |
| `249 / 250` | `됒.cs:3219-3220` | 每包有效数据 / 包长 |
| `3` | — | 3 字节/像素（`ꗳ`）；`ꔂ` 变体为 4 |

> 说明：任务描述里提到的 `0xC9` 在本方法、位置编码器以及整条 CC 发送链路上**均未出现**；0xCC 包内只有 `0xCC`(cmd)、`0xEF69`(魔数)、`0x80`(单 run)、`0xFC/0x0F`(位掩码)。

---

## 5. C# 参考实现（可直接编译）

```csharp
using System;

/// <summary>
/// 官方 깬.ꗳ(byte[], byte[], int, int, out int, int) 的等价实现。
/// reference = 参考帧（设备当前画面），current = 新帧（要显示的画面，像素数据取自它）。
/// 返回缓冲区容量 = reference.Length（官方行为）；失败返回 null 且 outLen = 0。
/// </summary>
public static byte[] EncodeDelta(byte[] reference, byte[] current,
                                  int bpp, out int outLen, int maxRunPixels = 65000)
{
    if (bpp <= 0) throw new ArgumentOutOfRangeException(nameof(bpp));

    int pixelCount = current.Length / bpp;
    byte[] dst = new byte[reference.Length];   // 官方：new byte[A_0.Length]

    int w = 0;            // 写游标
    int hdr = 0;          // 当前 run 的头偏移
    int run = 0;          // 当前 run 像素数
    bool open = false;    // 是否有未闭合 run
    bool bppIs4 = (bpp == 4);
    outLen = 0;

    for (int i = 0; i < pixelCount; i++)
    {
        int p = i * bpp;

        // --- 变化判定：B → G → R → A（短路） ---
        bool changed;
        if (reference[p] != current[p])            changed = true;
        else if (reference[p + 1] != current[p + 1]) changed = true;
        else if (reference[p + 2] != current[p + 2]) changed = true;
        else changed = bppIs4 && (reference[p + 3] != current[p + 3]);

        if (!changed)
        {
            if (!open) continue;                   // 未开 run：直接跳过

            if (run == 1)                          // 单像素 run 压缩为 6 字节
            {
                dst[hdr] |= 0x80;
                dst[hdr + 3] = dst[hdr + 5];
                dst[hdr + 4] = dst[hdr + 6];
                dst[hdr + 5] = dst[hdr + 7];
                w -= 2;
            }
            else                                   // 回填 16 位像素数（大端）
            {
                dst[hdr + 3] = (byte)(run >> 8);
                dst[hdr + 4] = (byte)run;
            }

            if (run > maxRunPixels)                // 官方：返回 null
            {
                outLen = 0;
                return null;
            }

            open = false; run = 0;
            continue;
        }

        if (!open)                                 // 开新 run：写 3 字节大端下标
        {
            dst[w]     = (byte)(i >> 16);
            dst[w + 1] = (byte)(i >> 8);
            dst[w + 2] = (byte)i;
            hdr = w;
            w += 5;
            open = true;
        }

        int a4 = bppIs4 ? (current[p + 3] >> 4) : 0x0F;
        dst[w]     = (byte)((current[p]     & 0xFC) | (a4 >> 2));
        dst[w + 1] = (byte)((current[p + 1] & 0xFC) | (a4 & 3));
        dst[w + 2] = current[p + 2];
        w += 3;
        run++;
    }

    if (open)                                      // 收尾：不压缩、不检查 maxRun
    {
        dst[hdr + 3] = (byte)(run >> 8);
        dst[hdr + 4] = (byte)run;
    }

    outLen = w;
    return dst;
}
```

配套的解码器（用于自测往返一致性）：

```csharp
/// <summary>把 EncodeDelta 的输出写回一张 BGRA 画布（仅用于校验）。</summary>
public static void ApplyDelta(byte[] delta, int deltaLen, byte[] canvas, int bpp)
{
    int i = 0;
    while (i < deltaLen)
    {
        int b0 = delta[i], b1 = delta[i + 1], b2 = delta[i + 2];
        int idx = ((b0 & 0x7F) << 16) | (b1 << 8) | b2;
        int count;
        int pay;
        if ((b0 & 0x80) != 0) { count = 1; pay = i + 3; i += 6; }
        else
        {
            count = (delta[i + 3] << 8) | delta[i + 4];
            pay = i + 5;
            i += 5 + 3 * count;
        }
        for (int k = 0; k < count; k++)
        {
            int q = (idx + k) * bpp;
            int B = delta[pay + 3 * k];
            int G = delta[pay + 3 * k + 1];
            int R = delta[pay + 3 * k + 2];
            int A4 = ((B & 3) << 2) | (G & 3);
            canvas[q]     = (byte)(B & 0xFC);
            canvas[q + 1] = (byte)(G & 0xFC);
            canvas[q + 2] = (byte)R;
            if (bpp >= 4) canvas[q + 3] = (byte)(A4 << 4);
        }
    }
}
```

---

## 6. 位置信息编码器 `ꔂ`（`깬.cs:5412`）

### 6.1 签名与语义

```csharp
public static byte[] ꔂ(byte[] A_0, int A_1, int A_2, out int A_3, int A_4 = 4, int A_5 = 65000)
```

| 参数 | 语义 | 官方调用值 |
|---|---|---|
| `A_0` | 帧像素缓冲 BGRA（**会被就地修改**，见 §6.4） | 921600 |
| `A_1` | **width** | 480 |
| `A_2` | **height** | 480 |
| `A_3` | `out`，返回时 = 已写入字节数（含尾部怪癖，见 §6.3） | — |
| `A_4` | 每像素字节数 | 4 |
| `A_5` | 单条记录的像素数上限 | 65000 |

初始化：`n = width*height`，`dst = new byte[n * bpp]`（即 921600），扫描 `i = 0 .. n-1`。

### 6.2 输出格式

**没有像素数据**，只有区间表，每条 **5 字节**：

| 偏移 | 长度 | 含义 |
|---|---|---|
| +0 | 1 | `(start >> 16) & 0xFF` |
| +1 | 1 | `(start >> 8) & 0xFF` |
| +2 | 1 | `start & 0xFF` |
| +3 | 1 | `count >> 8` |
| +4 | 1 | `count & 0xFF` |

语义：**`alpha > 15` 的极大连续像素区间**（即 4 位 alpha ≠ 0 的区域，也就是"不透明区")。区间之间必有 ≥1 个透明像素（或被 65000 上限切断，见 §6.4）。

> 注意与 `ꗳ` 的结构差异：这里长度字段紧跟 3 字节下标，**没有 0x80 压缩形态**，也**没有 0x80 标记位**。

### 6.3 `A_3`（out 长度）与尾部 5 个 0 字节的怪癖

正常闭合（遇到透明像素，`case 2`，`깬.cs:5564-5573`）：写回 `count` 后 **`num3 += 5`**（`깬.cs:5570`）。

**帧尾收尾**（`case 20`，`깬.cs:5491-5497`）：

```csharp
array[num5 + 3] = (byte)(num6 >> 8);
array[num5 + 4] = (byte)((uint)num6 & 0xFFu);
num3 += 10;          // ← 只写了 5 字节头，却把游标推了 10
```

由于 `num3` 只在闭合时前进，收尾时 `num3 == num5`，所以 `A_3 = num5 + 10`，而实际只写了 `num5+5` 字节 → **尾部多出 5 个 `0x00`**，且这 5 个字节被计入 `A_3`。

**实测证据**：`SamaEncoder\sq.pos`（27 字节）解码为：

```
000000FDE8 | 00FDE9FDE8 | 01FBD2FDE8 | 02F9BB8A45 | 0000000000 | EF69
 (0,65000) | (65001,65000)| (130002,65000)|(195003,35397)|  (0,0)   | magic
```

最后一条 `00 00 00 00 00` 就是怪癖产物；4 条真实区间恰好覆盖 `0..230400`。
日志中 298 个带 pos 的 0xCC 包**全部**带这 5 个 0；posLen = 0 的 1917 个包则没有。

> 复现建议：**照抄这个行为**（尾部补 5 个 0 并把长度 +5），设备显然能接受；若嫌脏，去掉这 5 字节并把 `posLen` 减 5 也应能工作（解析端遇到 `count==0` 会自然结束）。

### 6.4 65000 切分与对输入缓冲区的副作用

`case 17`：每加入一个像素 `num6++`，随后 `flag = (num6 >= A_5)`；成立时 `case 4`：

```csharp
A_0[(num4 + 1) * A_4 + 3] = 0;   // 直接把"下一个像素"的 alpha 改成 0
```

于是下一次迭代该像素被判为透明 → 关闭当前 run → 该像素被**跳过** → 再下一个像素开启新 run。
所以每个 65000 边界处会**丢掉 1 个像素**（实测 `0 / 65001 / 130002 / 195003` 的起始下标正好印证：65000、130001、195002 被跳过）。

⚠️ 副作用有两点必须在复刻时注意：
1. 它会**修改调用方传入的帧缓冲**（把某些像素的 alpha 置 0）。若同一帧还要用于别处（例如下一帧的参考帧），必须传副本。
2. 若 `num4 + 1 == width*height`（最后一像素恰好触发切分），会**数组越界抛异常**。

### 6.5 参考实现

```csharp
/// <summary>
/// 官方 깬.ꔂ(byte[], int width, int height, out int, int bpp, int maxRunPixels) 的等价实现。
/// 注意：为了与官方逐字节一致，本函数会就地把 src 中"切分点后一个像素"的 alpha 置 0。
/// 若不想污染调用方数据，请传入副本（并保留同样的跳过逻辑）。
/// </summary>
public static byte[] EncodePos(byte[] src, int width, int height,
                               out int outLen, int bpp = 4, int maxRunPixels = 65000)
{
    int n = width * height;
    byte[] dst = new byte[n * bpp];

    int w = 0, hdr = 0, run = 0, i = 0;
    bool open = false;

    while (i < n)
    {
        bool opaque = src[i * bpp + 3] > 15 && run < maxRunPixels;

        if (!opaque)
        {
            if (open)
            {
                dst[hdr + 3] = (byte)(run >> 8);
                dst[hdr + 4] = (byte)run;
                w += 5;
                open = false;
                run = 0;
            }
            i++;
            continue;
        }

        if (!open)
        {
            dst[w]     = (byte)(i >> 16);
            dst[w + 1] = (byte)(i >> 8);
            dst[w + 2] = (byte)i;
            hdr = w;
            open = true;
        }

        run++;
        if (run >= maxRunPixels && i + 1 < n)
            src[(i + 1) * bpp + 3] = 0;   // 官方副作用：把下一像素改成透明
        i++;
    }

    if (open)
    {
        dst[hdr + 3] = (byte)(run >> 8);
        dst[hdr + 4] = (byte)run;
        w += 10;                          // 官方 +10：多出 5 个 0 字节
    }

    outLen = w;
    return dst;
}
```

---

## 7. 0xCC / 0xD0 组包方式（发送侧，供新服务直接照做）

来自 `됒.cs:2725`（组头包）与 `됒.cs:2855`（流式发数据），与抓包完全一致。

### 7.1 250 字节固定包

```
+0   cmd        (1B)
+1   EF 69      (2B 魔数)
+3   param      (4B 大端)
+7   flag       (1B)
+8   保留       (2B，恒 0)
+10  应用头     (8B)      ← CC 包才用：seq(4B BE) + posLen(4B BE)
+18  ...        (232B，恒 0)
```

### 7.2 0xCC（增量帧）

```
CC EF 69 [param=deltaLen+posLen+2] 00 | 00 00 | [seq:4B BE] [posLen:4B BE] | 0...
随后 ceil(param/249) 个数据包：前 249 字节为 payload，第 250 字节补 0
payload = delta(长度 deltaLen) ‖ pos(长度 posLen) ‖ EF 69
```

* `seq`：每帧自增，抓包中为 0,1,2,...（`seq = 0` 的首帧也表示"相对 0xC8 全帧的差量"）。
* `posLen = 0` 时 payload 只有 `delta ‖ EF 69`，`param = deltaLen + 2`。
* 数据流每 24900 字节（100 包）`Thread.Sleep(1)`。
* 抓包实例（`spy_serial.log` 21:27:31 附近）：
  `CC EF 69 00 00 4D BF 00 00 00 | 00 00 00 00 | 00 00 2A F3` → param=19903、seq=0、posLen=10995，则 deltaLen=8906。

### 7.3 0xD0（位置信息，单独发送）

```
D0 EF 69 [param=posLen+2] 00 | 其余全 0
随后数据包 = pos ‖ EF 69
```

抓包中 `D0 param=10997`（=10995+2），紧跟在 921600 字节全帧之后发送。

### 7.4 无变化帧的兜底

`뽞.cs:6887`：当编码器返回长度 0 时，官方**不**发空 payload，而是构造 6 字节：

```csharp
array2[0] = 0x80;  A_2 = 6;   // 单像素 run @ 下标0，像素值 00 00 00
```

即 `80 00 00 00 00 00` —— 一个"把 (0,0) 写成黑色"的空操作记录。
抓包中共有 **511** 个这样的 6 字节帧，内容**全部**是 `80 00 00 00 00 00`，与上述兜底逻辑完全吻合。

---

## 8. 实测验证（2200+ 真实帧，非推测）

数据来源：`E:\AIWorkSpace\spy\spy_serial.log`（205 MB，逐行流式解析，未整体载入内存）。
方法：按 §7 的组包规则重组 0xCC 报文，剥离 `param/seq/posLen/EF69`，再按 §3 的格式解析。

### 8.1 完整性

| 检查项 | 结果 |
|---|---|
| 提取到的 0xCC 帧 | **2215** |
| delta 段逐字节解析成功（正好吃到末尾，无残留/无越界） | **2215 / 2215** |
| pos 段解析成功 | **2215 / 2215** |
| 尾部 `EF 69` 正确 | **2215 / 2215** |
| 解析出的 run 总数 | 1,093,186 |
| 其中 0x80 单像素形态 | 261,272（23.9%） |
| run 长度 min / 中位 / 均值 / max | 1 / 3 / 4.7 / 60 |
| delta 字节数 min / 中位 / max | 6 / 8,936 / 44,193 |
| pos 字节数 | 0（1917 帧）或 ≤ 11,250（298 帧） |

> 任何一处格式理解错误（长度字段宽度、0x80 位置、字段字节序、单像素形态长度）都会让后续记录错位——2215 帧全部精确对齐到最后一个字节，等价于对格式的强证明。

### 8.2 3 字节像素格式的独立交叉验证

`ꗳ` 的负载 alpha 半字节（`((B&3)<<2)|(G&3)`）与 `ꔂ` 生成的 pos 掩码（`alpha > 15`）应当**完全一致**——因为官方两处用的是同一个缓冲区。

对 298 个同时带 pos 的帧逐像素比对：

| 结果 | 数量 |
|---|---|
| 一致（半字节>0 ⟺ 落在 pos 区间内） | **1,342,723** |
| 不一致 | **0** |

同时验证了 `alpha ≤ 15 ⟺ A4 == 0` 的阈值语义完全自洽。

### 8.3 `A_0` / `A_1` 角色判定（重要）

用现有 helper 的三个产物 `sq_cur.bgra` / `sq_prev.bgra` / `sq.bin` 做判定（helper 的实参顺序是 `A_0=current, A_1=previous`）：

| 负载像素与谁一致 | 数量 |
|---|---|
| 与 `sq_prev.bgra` 一致（量化后比较） | **40,401 / 40,401** |
| 与 `sq_cur.bgra` 一致 | **0** |

两帧实际差异像素恰好 40,401 个。结论：**编码器输出的像素值全部来自 `A_1`**。
而 `SamaEncoder\Program.cs:55` 传的是 `{ current, previous, 0, 4, 0, 65000 }` → `A_1 = previous` → **helper 编出来的是上一帧**。新服务必须写成：

```csharp
byte[] delta = EncodeDelta(previousFrame, currentFrame, 4, out int deltaLen);
```

### 8.4 位置编码器的独立验证

`SamaEncoder\sq.pos`（27 字节，由 helper 对一张**全不透明**的帧生成）解码结果见 §6.3：
4 条区间 `(0,65000) (65001,65000) (130002,65000) (195003,35397)` + 5 个 0 + `EF69`。
既验证了 5 字节记录格式，也验证了 65000 切分时"跳 1 像素"的行为。

---

## 9. 与已知现象的对照

| 已知现象 | 解释 |
|---|---|
| `0xCC` 的 `param` ≈ 6KB–50KB | delta 中位 8,936 B；run 中位仅 3 像素，5 字节头占比很高 |
| 每 250 字节包 = 249 数据 + 1 个 0 | `됒.cs:2855` 每次 `new byte[250]`，只拷 249 字节 |
| 0xC8 全帧 921600 = 480×480×4 | 全帧是 4B/px；增量是 3B/px（rom 1.88 路径） |
| 每个连接只能提交一次 0xC8 | 设备侧限制，与编码器无关；后续靠 0xCC 累积更新 |
| 响应 `needReSend:0\|renderCnt:N` | 与本编码器无关，属重传层 |

---

## 10. 陷阱与注意事项（复刻时逐条核对）

1. **`A_0`/`A_1` 顺序**
   像素数据取自 `A_1`。传反了不会报错，只会让画面**永远滞后一帧**（或干脆不动）。现有 `SamaEncoder.exe` 就是反的。

2. **`A_2` 是死参数**
   方法体内零引用，传 0。

3. **`65000` 是像素数不是字节数**
   对应 195,000 字节负载。且超限检查只发生在"被未变化像素闭合"的时刻；**帧尾收尾不检查**。超限时官方返回 `null` 且 `A_4 = 0`，上层会误判为"无变化"并退化成 6 字节空操作帧（`뽞.cs:6887`）。

4. **`A_3 != 4` 时行为不纯**
   `case 3` 无条件读 `A_0[i*A_3+3]`/`A_1[i*A_3+3]`，当 `A_3=3` 时读到的是**下一个像素的 B**，最后一像素还会越界。本设备固定 4，不要用 3。

5. **`0x80` 标记与下标上限**
   下标只有 23 位可用，像素总数必须 < 8,388,608。480×480 安全；4K（3840×2160 = 8.29 M）已接近极限，8K 会撞车。

6. **输出容量按 `A_0.Length` 分配**
   `ꗳ` 的最坏情况约 3.7 B/px（两像素 run + 1 透明像素循环：`(5+6)/3`），< 4 B/px 安全。
   `ꔂ`（4B 变体）最坏约 4.33 B/px > 4 B/px，**理论上会数组越界**——这是官方潜在缺陷，若将来支持 rom ≥ 1.89 需自行扩容。

7. **帧尾不做单像素压缩**
   若最后一 run 长度恰为 1，输出是 8 字节（类型 A，`N=1`）而非 6 字节。解码端必须同时支持两种。

8. **`EF 69` 不在编码器输出里**
   自己追加在 `delta ‖ pos` 之后，并把 `param` 算上这 2 字节。

9. **位置编码器会改写输入缓冲**
   `src[(i+1)*bpp+3] = 0`。若这块内存要复用（例如作为下一帧的参考帧），必须传副本；并且要复刻"跳 1 像素"的分段起点（65001 / 130002 / 195003...）。

10. **位置包尾部 5 个 0 字节**
    `A_3` 比实际写入多 5。要么照抄，要么同步把 `posLen` 减 5，切忌长度与实际字节数不一致。

11. **不要每帧重连串口 / 每帧重发 0xC8**
    0xC8（或 0xCA）全帧每连接只发一次；之后全部用 0xCC 增量。

12. **`seq` 与 `posLen` 在包里是"应用头"，不是 payload**
    它们位于 0xCC 包的第 10–17 字节，**没有**被计入 `param`，也**不在** 249 字节数据流里。这是最容易写错的一处。

---

## 11. 最小可用发送流程（伪代码）

```
连接后（只需一次）：
    send(cmd=0x96, param=1)
    send(cmd=0x86, param=1)
    send(cmd=0xC8/0xCA, param=921600); stream(firstFrameBGRA)   // 480*480*4
    (可选) send(cmd=0xD0, param=posLen+2); stream(pos ‖ EF69)

每帧：
    delta = EncodeDelta(prevFrame, curFrame, 4, out deltaLen)   // 注意顺序！
    if (delta == null || deltaLen == 0) { delta = {0x80,0,0,0,0,0}; deltaLen = 6; }
    pos   = needPos ? EncodePos(curFrameCopy, 480, 480, out posLen) : empty
    payload = delta[0..deltaLen] ‖ pos[0..posLen] ‖ {0xEF,0x69}
    sendHeader(cmd=0xCC, param=payload.Length, seq=++seq, posLen=posLen)
    streamPayload(payload)          // 249+1 分片，每 100 包 sleep 1ms
    prevFrame = copy(curFrame)      // 保证下一帧的比较基准正确
```

---

## 12. 附：证据文件位置

| 内容 | 路径 |
|---|---|
| 反编译源（目标方法） | `E:\AIWorkSpace\decompiled\깬.cs:5812`、`:5412`、`:6134` |
| 调用点 / 组包 | `E:\AIWorkSpace\decompiled\뽞.cs:6769`、`E:\AIWorkSpace\decompiled\됒.cs:2725`、`:2855` |
| 上层调度 | `E:\AIWorkSpace\decompiled\UsbMonitorL\Monitor.cs:5740` |
| 串口抓包 | `E:\AIWorkSpace\spy\spy_serial.log`（2215 个 0xCC 帧） |
| helper（实参顺序反了） | `E:\AIWorkSpace\SamaEncoder\Program.cs:55` |
| helper 产物（用于 A_0/A_1 判定） | `E:\AIWorkSpace\SamaEncoder\sq_cur.bgra`、`sq_prev.bgra`、`sq.bin`、`sq.pos` |
