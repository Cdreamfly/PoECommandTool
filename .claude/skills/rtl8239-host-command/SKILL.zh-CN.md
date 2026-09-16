---
name: rtl8239-host-command
description: RTL8239 PoE Controller host command (UART/I2C) protocol reference, based on Realtek Host Command Guide Rev.2.2. Invoke when developing, reviewing or debugging firmware code that communicates with RTL8239 (e.g. sending config commands 0x00-0x19, status get commands 0x40-0x50, firmware download 0xC0/0xCA, or debug register access 0xF0/0xF1).
---

# RTL8239 PoE 控制器主机命令参考（中文版）

> **本文档为 [SKILL.md](SKILL.md) 的中文译本**，内容基于 Realtek《RTL8239 PoE Controller Host Command Guide》Rev.2.2（2025-08-29）。

## 1. 概述

RTL8239 是一款以太网供电（PoE）PSE 控制器。主机 MCU（如本项目中的 HC32L021）通过 UART 或 I2C 使用本 skill 所述的命令协议与其通信。

### N×8 口管理型 PSE 应用方案（图 1-1）

- 主机 CPU（交换机 CPU）通过 **UART** 与外部 CPU 通信（两者之间带隔离）。
- 外部 CPU 通过 **I2C** 接口控制一颗或多颗 RTL8239 PoE 芯片。
- 每颗 RTL8239 通过 UTP 连接到 PD（受电设备）。

```
HOST <--(隔离)--> 外部CPU <--I2C--> RTL8239 #1..#N <--UTP--> PD
```

## 2. 通信协议

### 2.1 UART 接口（表 2-1）

| 类型 | 参数 |
|------|------|
| 波特率 | 115200 |
| 停止位 | 1 |
| 校验位 | 无 |
| 流控 | 无 |
| 帧间隔（Inter Frame Gap） | >20ms |
| 超时 | >50ms |
| 包内字节间间隔 | <2ms |

- 普通命令在 50ms 内响应；**CMD 0x00 和 0x05 需要更长时间**。

### 2.2 I2C 接口

- 最大 I2C 速率：**1 MHz**。
- 从机 MCU 的 7 位设备地址：固定 **0x20**。
- 请求 = 连续写（图 2-1）：`S | A6..A0 + W | ACK | Data0 | ACK | Data1 | ACK | ... | DataN | ACK | P`
- 响应 = 连续读（图 2-2）：`S | A6..A0 + R | ACK | Data0 | ACK | Data1 | ACK | ... | DataN | NACK | P`
- 请求帧与响应帧的 App 命令载荷限制为 **12 字节**；**App Download CMD** 的最大请求载荷为 **36 字节**。
- 请求帧与响应帧之间的帧间隔建议 **> 20ms**，但 CMD 0x00 和 0x05 需要更长时间。

### 2.3 协议（表 2-2 / 2-3）

典型的请求 <-> 响应流程。主机 CPU 发出请求，并在预定义时间内等待外部 CPU 的响应。大多数命令为 **12 字节**。

请求帧：

| Byte0 | Byte1 | Byte2 – Byte10 | Byte11 |
|-------|-------|----------------|--------|
| 命令 ID | 序列号 | <=====数据=====> | 校验和 |

响应帧：

| Byte0 | Byte1 | Byte2 – Byte10 | Byte11 |
|-------|-------|----------------|--------|
| 响应 ID | 序列号 | <=====数据=====> | 校验和 |

校验和：从偏移 0 到 length-1 的所有字节累加，不考虑进位位（即 8 位累加和）：

```
Checksum = (Byte0 + Byte1 + ... + Byte(len-1)) & 0xFF
```

建议的命令收发流程（图 2-1）：

- **UART**：发送 CMD → 等待直到收到足够的响应字节（同时计算等待时间；超时溢出 → 返回错误）→ 校验通过 → 返回 Ok，否则返回错误。
- **I2C**：发送 CMD → **等待 25ms** → 读取 CMD 响应 → 校验响应 → 若校验失败（NG），重试次数 Try Times++ 并重试，直到达到最大重试次数 → 返回 Ok / 返回错误。
- **响应校验**：验证响应的 **Byte[1]（序列号）** 和 **校验和**。

## 3. 命令列表

命令分为四类：配置设置命令、配置与状态获取命令、杂项命令、调试命令（表 3-1）。

### 配置设置命令

| CMD ID | 名称 | 描述 | 范围 | 文档章节 |
|--------|------|-------------|-------|-------|
| 0x00 | Global Enable Set | 使能/禁用所有端口。 | App | 4.1 |
| 0x01 | Port Enable Set | 使能/禁用指定端口。 | App | 4.2 |
| 0x02 | Global Reset Set | 复位所有端口。 | App | 4.3 |
| 0x03 | Port Reset Set | 复位指定端口。 | App | 4.4 |
| 0x04 | Global Power Source Set | 设置功率预算。 | App | 4.5 |
| 0x05 | Port Mapping Enable Set | 使能/禁用端口映射。 | App | 4.6 |
| 0x06 | Port Pair Mapping Set | 设置端口映射关系。 | App | 4.7 |
| 0x08 | Port Function Mode Set | 选择端口功能模式。 | App | 4.8 |
| 0x09 | Port Detection Type Set | 设置端口检测类型。 | App | 4.9 |
| 0x0A | Port Detection Trigger Set（已废弃，仅 manual 模式） | 强制端口执行一次检测。 | App | 4.10 |
| 0x0B | Port Class Trigger Set | 强制端口执行一次分级。 | App | 4.11 |
| 0x0C | Port Inrush Mode Set | 设置端口浪涌（inrush）限制。 | App | 4.12 |
| 0x0D | Port Force Inrush Set | 另一种设置 inrush/上电限制的方式。 | App | 4.13 |
| 0x0E | Global Parameters Set | 设置 UVLO/OVLO 阈值。 | App | 4.14 |
| 0x0F | Port Disconnect Type Set | 设置断连类型（是否带延时）。 | App | 4.15 |
| 0x10 | Global Power Management Mode Set | 设置功率管理模式。 | App | 4.16 |
| 0x11 | Global Power Management Mode Extended Set | 设置系统预分配功能。 | App | 4.17 |
| 0x12 | Port Max Power Type Set | 设置端口最大功率阈值类型。 | App | 4.18 |
| 0x13 | Port Max Power Value Set（最大 51W） | 设置端口最大功率阈值，单位 **0.2W/LSB**。 | App | 4.19 |
| 0x14 | Port Max Power Value Extended Set（最大 102W） | 设置端口最大功率阈值，单位 **0.4W/LSB**。 | App | 4.20 |
| 0x15 | Port Priority Set | 设置端口优先级。 | App | 4.21 |
| 0x16 | Global Port Event Mask Set | 设置端口事件掩码。 | App | 4.22 |
| 0x18 | Port Trigger Det CLS PWR Set（仅 Manual 模式） | 强制端口上电。 | App | 4.23 |
| 0x19 | Port Cable Type Set（仅 4Pair 模式 RTL8239C） | _（在目录中；未列入表 3-1）_ | App | 4.24 |

### 配置与状态获取命令

| CMD ID | 名称 | 描述 | 范围 | 文档章节 |
|--------|------|-------------|-------|-------|
| 0x40 | Global Status Get | 获取全局状态。 | App | 5.1 |
| 0x41 | Global Power Status Get | 获取功率状态。 | App | 5.2 |
| 0x42 | Port Status Get | 获取指定端口的详细状态。 | App | 5.3 |
| 0x43 | Port Group Status Get | 获取端口组的基本状态。 | App | 5.4 |
| 0x44 | Port Measurement Get | 获取端口电压/电流/功率；获取 IC 中心温度。 | App | 5.5 |
| 0x45 | Port Mib Counter Get | 获取端口 mib 计数器信息。 | App | 5.6 |
| 0x46 | Port Event Status Get | 获取端口事件状态。 | App | 5.7 |
| 0x47 | Global Reset Reason Get | 获取全局复位原因。 | App | 5.8 |
| 0x48 | Port Basic Configuration Get | 获取端口基本信息（auto/semi 等）。 | App | 5.9 |
| 0x49 | Port Extended Configuration Get | 获取端口扩展信息。 | App | 5.10 |
| 0x4A | Global Parameters Get | 获取全局参数。 | App | 5.11 |
| 0x4B | Global PM Configuration Get | 获取功率管理配置。 | App | 5.12 |
| 0x4C | Global Device Address Get | 获取设备地址。 | App | 5.13 |
| 0x4D | Port Function Mode Get | 获取端口检测类型（表 3-1 中名为 "Port Detection Type Get"）。 | App | 5.14 |
| 0x4E | Channel Status Get | 获取两个通道的基本状态。 | App | 5.15 |
| 0x4F | Port Channel Voltage Current Get | 获取端口两个通道的电压/电流。 | App | 5.16 |
| 0x50 | System Chip Type Information Get | 获取 PoE 子系统所有 PSE 芯片类型信息。 | App | 5.17 |

### 杂项命令

| CMD ID / SUB ID | 名称 | 描述 | 范围 | 文档章节 |
|-----------------|------|-------------|-------|-------|
| 0xC0-00 | Jump To Loader | 子命令（00）。 | App | 6.1 |
| 0xC0-01 | Configuration Information Save | 子命令（01）。 | App | 6.2 |
| 0xC0-02 | Configuration Information Clear | 子命令（02）。 | App | 6.3 |
| 0xC0-03 | Configuration Version Save（仅 RTL8238B/8239） | 子命令（03）。 | App | 6.4 |
| 0xC0-04 | Configuration Version Get | 子命令（04）。 | App | 6.5 |
| 0xC0-05 | Configuration Information Reset | 子命令（05）。 | App | 6.6 |
| 0xC0-06 | Configuration Information Initial | 子命令（06）（表 3-1 标注为 "Save"，疑为文档笔误）。 | App | 6.7 |
| 0xC0-80 ~ 0xC0-83 | App Download | 子命令（0x80~83），仅用于 loader。 | **Loader** | 6.8 |
| 0xC0-40 | Jump To App | 子命令（0x40），仅用于 loader。 | **Loader** | 6.9 |
| 0xCA | Firmware Download | 下载固件到外部 flash。 | **Loader** | 6.10 |

### 调试命令

| CMD ID | 名称 | 描述 | 范围 | 文档章节 |
|--------|------|-------------|-------|-------|
| 0xF0 | Chip Register Set | 直接设置指定寄存器。 | App | 7.1 |
| 0xF1 | Chip Register Get | 直接读取指定寄存器。 | App | 7.2 |

## 4. 配置设置命令 — 详解

> **通用说明**：**APP** 下使用的命令中，未使用字节的默认值需为 **0xFF**（适用于所有命令）。
> STS 字段通用值：0x00 = 请求成功；0x01 = 请求失败。
> 逻辑端口索引：0x00-0x2F 有效（端口 0-47）；0x30-0xFF 无效。

### 4.1 Global Enable Set CMD (0x00) [App]

使能或禁用所有端口。Disable：所有端口保持 idle 状态，不做检测与分级。Enable：所有端口正常检测与分级，并有机会上电。**响应在 1 秒内返回；响应返回前不得发送任何命令。**

Request（表 4-1）：`CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL：0x00 = 禁用所有端口功能；0x01 = 使能所有端口功能；0x02-0xFF = RSVD

Response（表 4-2）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.2 Port Enable Set CMD (0x01) [App]

使能或禁用单个端口。

Request（表 4-3）：`CMD(0) | SEQ(1) | Port(2) | VAL(3) | Port(4) | VAL(5) | Port(6) | VAL(7) | Port(8) | VAL(9) | RSVD(10) | CHKSM(11)` — 最多 4 组 Port/VAL
- VAL：0x00 = 禁用端口功能；0x01 = 使能端口功能；**0x02 = semi-auto 模式下强制端口上电**；0x03-0xFF = RSVD

Response（表 4-4）：`CMD(0) | SEQ(1) | Port(2) | STS(3) | Port(4) | STS(5) | Port(6) | STS(7) | Port(8) | STS(9) | RSVD(10) | CHKSM(11)` — 4 组 Port/STS

### 4.3 Global Reset Set CMD (0x02) [App]

复位整个 PoE 子系统。

Request（表 4-5）：`CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL：0x00 = 不复位 PoE 系统；0x01 = 复位整个 PoE 系统

Response（表 4-6）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.4 Port Reset Set CMD (0x03) [App]

将端口状态机复位到 idle，配置值恢复为默认值。

Request（表 4-7）：`CMD(0) | SEQ(1) | Port(2) | VAL(3) | Port(4) | VAL(5) | Port(6) | VAL(7) | Port(8) | VAL(9) | RSVD(10) | CHKSM(11)` — 最多 4 组 Port/VAL
- VAL：0x00 = 不复位端口；0x01 = 复位端口

Response（表 4-8）：`CMD(0) | SEQ(1) | Port(2) | STS(3) | Port(4) | STS(5) | Port(6) | STS(7) | Port(8) | STS(9) | RSVD(10) | CHKSM(11)`

### 4.5 Global Power Source Set CMD (0x04) [App]

设置指定功率 bank 的系统功率池（power bank）和保留功率值。**Reserved Power 必须小于 Total Power。**

Request（表 4-9）：`CMD(0) | SEQ(1) | Bank ID(2) | Total Power(3-4) | Reserved Power(5-6) | RSVD(7-10) | CHKSM(11)`
- Bank ID：0x00-0x07 = 有效 bank id；0x08-0xFF = 无效（请求失败）
- Total Power：单位 **0.1W/LSB**
- Reserved Power：单位 **0.1W/LSB**

Response（表 4-10）：`CMD(0) | SEQ(1) | Bank ID(2) | STS(3) | RSVD(4-10) | CHKSM(11)`
- Bank ID：0x00-0x07 = 有效；0x08-0xFF = 无效

### 4.6 Port Mapping Enable Set CMD (0x05) [App]

设置系统逻辑端口到物理端口映射的使能状态。**端口映射前和映射结束后都应下发本命令。** 响应在 **2 秒**内返回；响应返回前不得发送任何命令。
- 端口映射开始时（VAL=0x00）：所有运行时 PoE 配置将被**复位**。
- 端口映射结束时（VAL=0x01）：所有运行时 PoE 配置（含端口映射）将被**保存到 MCU flash**。

Request（表 4-11）：`CMD(0) | SEQ(1) | VAL(2) | MaxPort(3) | RSVD(4-10) | CHKSM(11)`
- VAL：0x00 = 禁用端口映射，端口映射开始；0x01 = 使能端口映射，端口映射结束
- Max Port：0x00-0x30 = 有效端口数（0-48）；0x31-0xFF = 无效

注意：
- 端口映射会**将 PoE 配置恢复为默认值**，因此端口映射之前的配置才会生效。
- 端口映射流程：开始时必须先用 CMD-0x05 设为禁用（VAL=0x0），结束后再用 CMD-0x05 设为使能（VAL=0x1）。

Response（表 4-12）：`CMD(0) | SEQ(1) | VALSTS(2) | MaxPort STS(3) | RSVD(4-10) | CHKSM(11)`
- VALSTS：0x00 = 成功；0x01 = 失败
- MaxPortSTS：0x00 = 成功；0x01 = 失败

### 4.7 Port Pair Mapping Set CMD (0x06) [App]

为指定逻辑端口配置设备索引（device index）和通道索引。所有芯片 I2C 地址应在 **0x20 到 0x3E** 范围内，且为**偶数**。
- 若板级 I2C 地址从 0x20 起始且全部连续：byte[10] = **0xFF**，chip index =（芯片 I2C 地址 − 0x20）/ 2。
- 若板级地址不从 0x20 起始或地址不连续：byte[10] = 芯片 I2C 地址，且建议 chip index 按 I2C 地址越小取值越小。

Request（表 4-13）：`CMD(0) | SEQ(1) | Port(2) | 4-Pair Enable(3) | Chip index(4) | Pri-Channel(5) | Sec-Channel(6) | RSVD(7-9) | Addr(10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效
- 4-Pair Enable：0x00 = 2-pair 模式；0x01 = 4-pair 模式
- Chip index（DEVICE ID）：0x0-0xE
- Pri-Channel：0x0-0x7 = 4-pair 模式下 4-pair 端口的主通道，或 2-pair 模式下的通道索引
- Sec-Channel：0x0-0x7 = 4-pair 模式下 4-pair 端口的副通道
- Addr：芯片 I2C 地址（仅在板级地址不从 0x20 起始或不连续时使用；否则填 0xFF）

注意：
- **AT 应用**：Byte[3] 4-Pair Enable = 0x00（2-pair），Byte[6] Sec Channel = 0xFF。
- **BT 应用**：Byte[3] = 0x01（4-pair），Byte[5-6] 设置为对应通道。
- 端口映射会将 PoE 配置恢复为默认值，因此端口映射之前的配置才会生效。

Response（表 4-14）：字节布局与请求相同；每个字段回显各自的状态：
- Port：0x00-0x2F 有效；0x30-0xFF 无效
- 4-Pair Enable / Chip ID / Pri-Channel / Sec-Channel：0x00 = 成功；0x01 = 失败
- Addr：若板级地址不从 0x20 起始或不连续 → 0x00 = 成功，0xFF = 失败；若地址从 0x20 起始且连续 → 0xFF = 不关心

### 4.8 Port Function Mode Set CMD (0x08) [App]

设置 PSE 端口功能模式：**semi-auto / auto / manual**。
- **Semi-auto**：端口自动完成检测与分级，但最终由主机 CPU 根据计算结果通知 PoE 控制器该 PD 是否可上电或返回 IDLE。
- **Auto**：端口自动完成检测与分级，PoE 控制器依据预设规则自行决定是否上电或返回 IDLE。
- **Manual**：端口可通过 CMD-0x18（Port Trigger Det CLS PWR）强制上电。

Request（表 4-15）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = Auto 模式；0x01 = Semi-auto 模式；0x02 = Manual 模式

Response（表 4-16）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.9 Port Detection Type Set CMD (0x09) [App]

决定是否对 Legacy PD 进行分级，以及分级失败的 PD 是否可上电。**做 Sifos 测试时 VAL 应设为 0/2/4** — 其他值会导致 **det_range 和 det_cc 失败**。

Request（表 4-17）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL = 0x00、0x02、0x04：对标准 PD 分级、**不**对 Legacy PD 分级；分级失败的 PD **不可**上电（0x0/0x2/0x4：功能无差异）
- VAL = 0x01、0x03、0x05：对标准 PD **和** Legacy PD 分级；分级失败的 PD **不可**上电（0x1/0x3/0x5：功能无差异）
- VAL = 0x06：对标准 PD **和** Legacy PD 分级；分级失败的 PD **可以**上电

Response（表 4-18）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.10 Port Detection Trigger Set CMD (0x0A，已废弃 — 仅 manual 模式) [App]

强制指定端口在 manual 模式下执行检测，并获取检测到的 PD 类型。

Request（表 4-19）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- **SEQ：0x00 = Set 命令；0x01 = Get 命令**（SEQ 兼作 set/get 选择）
- VAL（Set 命令）：0x00 = 不触发；0x01 = 触发
- VAL（Get 命令）：设为 0xFF

Response（表 4-20）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- STS（Set 命令）：0x00 = 成功；0x01 = 失败；0xFF = 无效值
- STS（Get 命令）：0x00 = 检测到有效 PD；0x01 = 检测到无效 PD；0xFF = 无效值

### 4.11 Port Class Trigger Set CMD (0x0B) [App]

强制指定端口在 auto 模式和 semi-auto 模式下执行分级。

Request（表 4-21）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = None；0x01 = 强制分级使能

注意：**本命令会导致 Sifos det_range 失败 — Sifos 测试时请勿使用。**

Response（表 4-22）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.12 Port Inrush Mode Set CMD (0x0C) [App]

设置端口的 inrush 模式，用于配置端口可支持的最大 PD 分级等级。

Request（表 4-23）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`

| VAL | 模式 | 最高 PD class | 最大 Iinrush | 最大 Ilim |
|-----|------|--------------|-------------|----------|
| 0x00 | IEEE 802.3af | class3 | 425mA | 425mA |
| 0x01 | IEEE 802.3af High Inrush | class3 | 850mA | 425mA |
| 0x02 | Pre-IEEE 802.3at | class4 | 425mA | 850mA |
| 0x03 | IEEE 802.3at | class4 | 425mA | 850mA |
| 0x04 | Pre-IEEE 802.3bt type3 | class6 | 850mA | 850mA |
| 0x05 | IEEE 802.3bt type3 | class6 | 425mA | 850mA |
| 0x06 | IEEE 802.3bt type4 | class8 | 425mA | 1275mA |
| 0x07 | Pre-IEEE 802.3bt type4 | class8 | 850mA | 1275mA |
| 0x09 | AT ALT B | class4 | 425mA | 850mA |

注意：**测试 AF/AT Sifos 时，inrush 模式应设为 0x00（802.3af）或 0x03（802.3at）。BT inrush 模式会导致 AF/AT Sifos det_time 失败。**

Response（表 4-24）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.13 Port Force Inrush Set CMD (0x0D) [App]

设置单个或多个端口的 inrush 是否提升。使能后 inrush 提升一级。inrush 档位：**212.5mA、425mA、850mA、1275mA**。
**本命令对 inrush 模式 0x01（802.3af High Inrush）、0x04（Pre-802.3bt type3）、0x07（Pre-802.3bt type4）无效。**

Request（表 4-25）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = Force Inrush 禁用；0x01 = Force Inrush 使能

注意：**Sifos 测试时请将 VAL 设为 0x00（Force Inrush 禁用）。**

Response（表 4-26）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.14 Global Parameters Set CMD (0x0E) [App]

设置 PoE 子系统的系统参数：UVLO 阈值和 OVLO 阈值。

Request（表 4-27）：`CMD(0) | SEQ(1) | UVLO(2) | RSVD(3) | OVLO(4) | RSVD(5-10) | CHKSM(11)`
- UVLO：**33V + UVLO × 64.45mV/LSB**
- OVLO：**57V + OVLO × 64.45mV/LSB**，最大值 60V → 0x00-0x2F 有效；0x30-0xFF 无效

注意：
- 若 OVLO 或 UVLO 任一为无效值 **0xFF，则两者都不生效**。
- 若 OVLO > 0x30 且 ≠ 0xFF，固件会将其默认设为最大值 **0x2F**。
- 由于换算原因，通过 CMD-0x4A 读回的 OVLO/UVLO 值可能会**减小 1**。

Response（表 4-28）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.15 Port Disconnect Type Set CMD (0x0F) [App]

设置指定端口或多个端口的断连类型。

Request（表 4-29）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = 禁用 MPS 能力；0x01 = 保留；0x02 = 使能 MPS 能力；0x03 = 使能 MPS 能力（**MPS 功能将在上电 700ms 后启用**）

注意：断连类型 = 0x00（禁用 MPS）时，**端口不会因低电流而关断**。

Response（表 4-30）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.16 Global Power Management Mode Set CMD (0x10) [App]

设置 PoE 子系统的全局功率管理模式。
- **Static 模式**：按端口**分级（class）**功耗进行功率管理。
- **Dynamic 模式**：按端口**实际**功耗进行功率管理。
- **Priority 模式**：高优先级端口可抢占低优先级端口完成上电；同优先级之间无抢占关系。端口优先级由 CMD-0x15 设置。

Request（表 4-31）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)` — byte2 字段名为 STS 但承载模式值：
- 0x00 = None；0x01 = Static 带优先级；0x02 = Dynamic 带优先级；0x03 = Static 不带优先级；0x04 = Dynamic 不带优先级

Response（表 4-32）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)` — STS 0x00 = 成功；0x01 = 失败

### 4.17 Global Power Management Mode Extended Set CMD (0x11) [App]

设置扩展全局功率管理模式：系统预分配（pre-allocated）功能和电流模式裕量功能。
- 预分配**使能**时：指定端口可上电的条件是系统剩余功率必须大于端口请求功率。
- 预分配**禁用**时：上电该端口不要求满足端口请求功率。

Request（表 4-33）：`CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL：0x00 = 使能系统预分配功能；0x01-0xFF = 禁用系统预分配功能

Response（表 4-34）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.18 Port Max Power Type Set CMD (0x12) [App]

设置单端口或多端口的最大功率类型。
- **Class based**：最大功率按分级等级确定；端口最大功率与 CMD-0x13/0x14 无关。**Sifos 测试应使用 class 模式。**
- **User defined**：最大功率由 CMD-0x13 或 CMD-0x14 设置。

Request（表 4-35）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = 保留；0x01 = Class based；0x02 = User defined

注意：**Sifos 测试时请将 VAL 设为 0x01。**

Response（表 4-36）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.19 Port Max Power Value Set CMD (0x13，最大 51W) [App]

设置指定端口或多个端口的最大功率值。最大值 = 0.2W × 255 = **51W**。
**仅在端口为 user defined 模式时生效**（CMD-0x12 VAL=0x02）；class 模式下无效。
建议 AT 最大功率 < 36W；BT 最大功率 < 98W。

Request（表 4-37）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：**0.2W/LSB**

Response（表 4-38）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

### 4.20 Port Max Power Value Extended Set CMD (0x14，最大 102W) [App]

设置指定端口或多个端口的最大功率值。最大值 = 0.4W × 255 = **102W**。
**仅在端口为 user define 模式时生效**（CMD-0x12 VAL=0x02）；class 模式下端口最大功率与 CMD-0x13、CMD-0x14 无关。
建议 AT 最大功率 < 36W；BT 最大功率 < 98W。

Request（表 4-39）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：**0.4W/LSB**

Response（表 4-40）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

### 4.21 Port Priority Set CMD (0x15) [App]

设置指定端口或多个端口的分配优先级。

Request（表 4-41）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = Low；0x01 = Medium；0x02 = High；0x03 = Critical

Response（表 4-42）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

### 4.22 Global Port Event Mask Set CMD (0x16) [App]

设置若干种全局端口事件掩码。事件状态被记录、中断脉冲发送给主机之前，必须先设置对应掩码。故障事件包括 **short、OVLO、UVLO 和 overload**。

Request（表 4-43）：`CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL 位图：
  - BIT0：保留
  - BIT1：Disconnect 事件掩码（0 = 禁用，1 = 使能）
  - BIT2：Fault 事件掩码：short、OVLO、UVLO 和 overload（0 = 禁用，1 = 使能）
  - BIT3-7：保留

Response（表 4-44）：`CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.23 Port Trigger Det CLS PWR CMD (0x18，仅 Manual 模式) [App]

强制指定端口在 manual 模式下上电。

Request（表 4-45）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = Port Reset；0x01 = 保留；0x02 = 保留；0x03 = Force power enable

Response（表 4-46）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.24 Port Cable Type Set CMD (0x19，仅 4Pair 模式 RTL8239C) [App]

设置 4Pair 模式下的线缆类型。**Normal 类型**的两个通道对应 4Pair；**Short Cable 类型**的两个通道对应 2Pair。

Request（表 4-47）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL：0x00 = Normal Cable；0x01 = Short Cable

Response（表 4-48）：`CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

## 5. 配置与状态获取命令 — 详解

### 5.1 Global Status Get CMD (0x40) [App]

获取 PoE 子系统的基本系统状态：PoE 模式、通信接口、最大端口数、port map、设备 ID、软件版本、MCU 类型、配置状态和扩展版本。

Request（表 5-1）：`CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response（表 5-2）：`CMD(0) | SEQ(1) | RSVD(2) | Max Ports(3) | Port Map(4) | Device ID(5-6) | SW Ver(7) | MCU Type(8) | Config Status(9) | Ext.Ver(10) | CHKSM(11)`
- Max Ports：PoE 系统的最大端口数
- Port Map：系统端口使能状态 — 0x00 = Disable；0x01 = Enable
- Device ID：16 位 — 0x0138 = RTL8238B；0x0238 = RTL8238C；0x0039 = RTL8239；0x0139 = RTL8239C
- SW Ver：8 位软件版本 — BIT7-4 = 主版本号；BIT3-0 = 次版本号
- MCU Type：8 位 — 0x00 = GigaDevice GD32F310XXXX；0x01 = GD32E230XXXX；0x02 = GD32F303XXXX；0x03 = GD32F103XXXX；0x04 = GD32E103XXXX；0x10 = Nuvoton M0516XXXX；0x11 = Nuvoton M0564XXXX；0x12 = Nuvoton NUC029XXXX
- Config Status 位图：
  - BIT0：配置状态位 — 0x0 = 配置为脏（dirty）；0x1 = 配置已保存
  - BIT1：系统复位位 — 0x0 = 未发生系统复位；0x1 = 发生过系统复位
  - BIT2：Global Disable Pin 指示 — 0x0 = Global Disable Pin 为低；0x1 = Global Disable Pin 为高
- Ext.Ver：8 位扩展软件版本 — BIT7-4 = 主版本号；BIT3-0 = 次版本号

### 5.2 Global Power Status Get CMD (0x41) [App]

获取 PoE 子系统的系统功率状态：系统已分配功率、系统可用功率、功率 bank id、系统当前功率。
- **Static 模式**：系统可用功率 = 系统总功率 − 系统已分配功率
- **Dynamic 模式**：系统可用功率 = 系统总功率 − 系统当前功率

Request（表 5-3）：`CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response（表 5-4）：`CMD(0) | SEQ(1) | System Allocated Power(2-3) | System Available Power(4-5) | Bank ID(6) | System Current Power(7-8) | RSVD(9-10) | CHKSM(11)`
- System Allocated Power：按端口分级等级分配的系统总功率（**0.1W/LSB**）
- System Available Power：当前 bank id 的系统可用功率（**0.1W/LSB**）
- Bank ID：系统使用的 Bank ID
- System Current Power：端口实际消耗的系统总功率（**0.1W/LSB**）

### 5.3 Port Status Get CMD (0x42) [App]

获取指定端口的基本信息：电源状态、故障状态、检测结果、分级结果和 PD 类型。

Request（表 5-5）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-6）：`CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)`
- STS1 — 端口电源状态或故障状态：0x00 = Disabled；0x01 = Searching；0x02 = Delivering Power；0x03 = RSVD；0x04 = Fault；0x05 = RSVD；0x06 = Requesting Power
- STS2 — **若 STS1 为 Fault 或 Other Fault，STS2 表示故障类型**：0x00 = OVLO；0x01 = MPS Absent；0x02 = Short；0x03 = Overload；0x04 = Power Denied；0x05 = Thermal Shutdown；0x06 = Inrush fail；0x07 = UVLO；0x0E = GOTP。
  **否则 STS2 表示检测与分级结果**：
  - BIT7-4 分级结果：0x0-0x8 = PD class 编号；0x9-0xB = 保留；0xC = PD 按 Class 0 处理；0xD = RSVD；0xE = Class Mismatch；0xF = Class over Current
  - BIT3-0 检测结果：0x0 = Unknown；0x1 = Short Circuit；0x2 = High Cap；0x3 = Rlow；0x4 = Valid PD；0x5 = Rhigh；0x6 = Open Circuit；0x7 = FET Failure；0x8-0xF = 保留
- STS3 — 若连接检查为 dual：BIT7-4 = 副通道分级结果，BIT3-0 = 主通道分级结果；否则 BIT7-0 = 有效通道分级结果
- STS4：RSVD
- STS5 — 连接检查结果：0x00 = 2pair；0x01 = Single PD；0x02 = Dual PD；0x03 = Unknown
- STS6 / STS7 / STS8：RSVD

注意：
1. 端口状态（STS1）为故障状态时，STS2 表示故障类型而非检测/分级结果。
2. 接入 dual PD 时，任一通道满足功率要求则 STS1 显示 Requesting Power；否则任一通道已上电时 STS1 显示 Delivering Power。
3. 接入 dual PD 且无故障事件时，任一通道检测到 legacy PD 则 STS2 的 Bit3-0 表示 legacy 检测结果。
4. 接入 dual PD 时，STS3 的 Bit3-0 表示主通道分级结果，Bit7-4 表示副通道分级结果。

### 5.4 Port Group Status Get CMD (0x43) [App]

一次获取一组端口的状态。所有端口按每组 **4 个端口**分组（如 8 口系统：Group 0 = 端口 0-3，Group 1 = 端口 4-7）。每个端口的信息：电源状态、故障状态、检测结果、分级结果和 PD 类型。

Request（表 5-7）：`CMD(0) | SEQ(1) | Group(2) | RSVD(3-10) | CHKSM(11)`
- Group：0x00-0x0B = 有效组索引（0-11）；0x0C-0xFF = 无效

Response（表 5-8）：`CMD(0) | SEQ(1) | Group(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)` — 每组 4 个端口：(STS1,STS2) = 第 1 个端口，(STS3,STS4) = 第 2 个，(STS5,STS6) = 第 3 个，(STS7,STS8) = 第 4 个
- STS1/STS3/STS5/STS7：
  - BIT7-4 检测结果：0x0 = Unknown；0x1 = Short Circuit；0x2 = High Cap；0x3 = Rlow；0x4 = Valid PD；0x5 = Rhigh；0x6 = Open Circuit；0x7 = FET Failure；0x8-0xF = 保留
  - BIT3-0 端口电源状态或故障状态：0x0 = Disabled；0x1 = Searching；0x2 = Delivering Power；0x3 = RSVD；0x4 = Fault；0x5 = RSVD；0x6 = Requesting Power
- STS2/STS4/STS6/STS8：
  - BIT7-4 故障类型（端口电源/故障状态 = Fault 时有效）：0x0 = OVLO；0x1 = MPS Absent；0x2 = Short；0x3 = Overload；0x4 = Power Denied；0x5 = Thermal Shutdown；0x6 = Inrush fail；0x7 = UVLO；0xE = GOTP
  - BIT3-0 分级结果（连接检查为 dual 时，该值为**两个通道分级结果之和**）：0x0-0x8 = PD class 编号；0x9-0xB = 保留；0xC = PD 按 Class 0 处理；0xD = RSVD；0xE = Class Mismatch；0xF = Class over Current

注意：
1. 接入 dual PD 时，任一通道满足功率要求则 STS1/STS3/STS5/STS7 的 Bit3-0 显示 Requesting Power；否则任一通道已上电时 Bit3-0 显示 Delivering Power。
2. 接入 dual PD 时，任一通道检测到 legacy PD 则 STS1/STS3/STS5/STS7 的 Bit7-4 显示 legacy 检测结果。
3. 接入 dual PD 时，STS2/STS4/STS6/STS8 的 Bit3-0 表示主通道与副通道分级结果之和。

### 5.5 Port Measurement Get CMD (0x44) [App]

获取指定端口的测量值：端口电压、电流、温度和实际消耗功率。

Request（表 5-9）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-10）：`CMD(0) | SEQ(1) | Port(2) | Voltage(3-4) | Current(5-6) | Temperature(7-8) | Power(9-10) | CHKSM(11)`
- Voltage：端口电压（**64.45mV/LSB**）
- Current：端口电流（**1mA/LSB**）
- Temperature：IC 中心温度 = **(Temperature − 120) × (−1.25) + 125**
- Power：端口实际消耗功率（**0.1W/LSB**）

### 5.6 Port Mib Counter Get CMD (0x45) [App]

获取指定端口的 MIB 计数器：MPS absent 计数器、Overload 计数器、Short 计数器、Power Denied 计数器和 Invalid Signature 计数器。
**当 MCU SRAM 小于 4K 时（如 NUC029 和 M0516），可能不支持 mib 计数器功能。**

Request（表 5-11）：`CMD(0) | SEQ(1) | Port(2) | Reset Flag(3) | RSVD(4-10) | CHKSM(11)`
- Reset Flag：0x00 = 读后不复位 mib 计数器；0x01 = 读后复位 mib 计数器

Response（表 5-12）：`CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | RSVD(8-10) | CHKSM(11)`
- STS1 = MPS absent Counter；STS2 = Overload Counter；STS3 = Short Counter；STS4 = Power Denied Counter；STS5 = Invalid Signature Counter

### 5.7 Port Event Status Get CMD (0x46) [App]

获取所有端口的事件状态：事件掩码配置、系统事件状态和所有端口的事件状态。

Request（表 5-13）：`CMD(0) | SEQ(1) | Clear Flag(2) | RSVD(3-10) | CHKSM(11)`
- Clear Flag：0x00 = 读后不清除端口事件状态；0x01 = 读后清除端口事件状态

Response（表 5-14）：`CMD(0) | SEQ(1) | Event Mask(2) | Event Status(3) | STS1[7-0](4) | STS2[15-8](5) | STS3[23-16](6) | STS4[31-24](7) | STS5[39-32](8) | STS6[47-40](9) | RSVD(10) | CHKSM(11)`
- Event Mask（全局掩码；事件状态被记录、中断脉冲发送给主机之前必须先设置）：
  - BIT0：RSVD；BIT1：Disconnect 事件掩码（0 = 禁用，1 = 使能）；BIT2：Fault 事件掩码（0 = 禁用，1 = 使能）；BIT3-7：保留
- Event Status（对于每个全局事件状态，若任一端口的该类事件发生，对应位将被置位）：
  - BIT0：RSVD；BIT1：Disconnect 事件状态（0 = 未发生，1 = 已发生）；BIT2：Fault 事件状态（0 = 未发生，1 = 已发生）；BIT3-7：保留
- STS1..STS6 = 48 位端口事件位图：STS1[7-0] 表示 port7 到 port0 的事件状态，STS2[15-8] 表示 port15 到 port8，依此类推。例如 STS1[0] = port0 事件状态 — 若 port0 的任一类事件状态被记录，STS1[0] 将被置位；STS1[7] = port7 事件状态。

### 5.8 Global Reset Reason Get CMD (0x47) [App]

获取系统复位原因。本命令可获取发生复位或异常的芯片的 I2C 地址。

Request（表 5-15）：`CMD(0) | SEQ(1) | Clear Flag(2) | RSVD(3-10) | CHKSM(11)`
- Clear Flag：0x00 = 读后不清除复位原因；0x01-0xFF = 读后清除复位原因

Response（表 5-16）：`CMD(0) | SEQ(1) | Reset Flag(2) | Error Addr(3) | Reset reason(4) | Error Addr 01(5) | Error Addr 23(6) | Error Addr 45(7) | Error Addr 67(8) | Error Addr 89(9) | Error Addr 10 11(10) | CHKSM(11)`
- Reset Flag：若所有芯片 I2C 接口正常，该字节 = **0x00**；否则 **bit[5-0]** = 第一个（出错的）I2C 地址，**bit[6]** 反映芯片复位状态
- Error Addr：若所有芯片 I2C 接口正常，该字节 = **0xFF**；否则该字节反映第一个出错的 I2C 地址
- Reset reason：0x01 = 上电复位；0x02 = nRST 复位；0x03 = 软件复位；0x04 = 其他错误复位
- Error Addr 01：
  - BIT7-4：chip0 访问错误标志 — 0xF = chip0 访问正常；0x0-0xE = chip0 I2C 地址的等价值（**芯片 I2C 地址 = 等价值 × 2 + 0x20**）
  - BIT3-0：chip1 访问错误标志 — 编码同上
  - Error Addr 23 及其他字节格式类似（每字节覆盖 2 颗芯片）

### 5.9 Port Basic Configuration Get CMD (0x48) [App]

获取指定端口的基本配置：使能状态、功能模式、检测类型、分级类型、断连类型和 pair 类型。

Request（表 5-17）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-18）：`CMD(0) | SEQ(1) | Port(2) | Enable Status(3) | Function Mode(4) | Det Type(5) | Cls Type(6) | DISCXNT Type(7) | Pair Type(8) | RSVD(9) | Cable Type(10) | CHKSM(11)`
- Enable Status：0x00 = Disabled；0x01 = Enabled
- Function Mode：0x00 = Auto 模式；0x01 = Semi-auto 模式；0x02 = Manual 模式
- Det Type（检测类型）：0x00/0x02/0x04 = 对标准 PD 分级；0x01/0x03/0x05 = 对标准及 legacy PD 分级；0x06 = 对过流 PD 分级
- Cls Type：RSVD
- Disconnect Type：0x00 = 禁用 MPS 能力；0x01 = 保留；0x02 = 使能 MPS 能力；0x03 = 使能 MPS 能力（**MPS 功能将在上电 700ms 后启用**）
- Pair Type：0x00 = Alternative A；0x01 = Alternative B
- Cable Type（4Pair 模式）：0x00 = Normal；0x01 = Short

### 5.10 Port Extended Configuration Get CMD (0x49) [App]

获取指定端口的扩展配置：PD inrush 模式、功率限制模式、功率阈值、优先级和端口映射数组。

Request（表 5-19）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-20）：`CMD(0) | SEQ(1) | Port(2) | Inrush Mode(3) | Limit Type(4) | Max Power(5) | Priority(6) | ChipAddr(7) | Pri-Chnl(8) | Sec-Chnl(9) | RSVD(10) | CHKSM(11)`
- Inrush Mode：0x00 = IEEE 802.3af；0x01 = IEEE 802.3af high inrush；0x02 = IEEE 802.3at compatible；0x03 = IEEE 802.3at；0x04 = Pre-IEEE 802.3bt type3 模式；0x05 = IEEE 802.3bt type3 模式；0x06 = IEEE 802.3bt type4 模式；0x07 = Pre-IEEE 802.3bt type4 模式；0x09 = IEEE 802.3at ALT B 模式
- Limit Type：0x01 = Class based；0x02 = User defined
- Max Power：端口最大功率阈值（**0.4W/LSB**）
- Priority：0x00 = Low；0x01 = Medium；0x02 = High；0x03 = Critical
- ChipAddr：芯片 I2C 地址
- Pri-Chnl：主通道；Sec-Chnl：副通道

### 5.11 Global Parameters Get CMD (0x4A) [App]

获取扩展系统配置：UVLO 阈值、预分配状态、上电模式、断连行为、检测标志、OVLO 阈值和 PSE 芯片数量。

Request（表 5-21）：`CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response（表 5-22）：`CMD(0) | SEQ(1) | UVLO(2) | Pre-alloc(3) | RSVD(4-6) | OVLO(7) | Chip Number(8) | Not Sup Chip(9) | RSVD(10) | CHKSM(11)`
- UVLO：UVLO 阈值 = **33V + UVLO × 64.45mV**
- Pre-alloc：系统预分配使能状态 — 0x00 = Disable；0x01 = Enable
- OVLO：OVLO 阈值 = **57V + OVLO × 64.45mV**
- Chip Number：检测到的 PSE 芯片数量
- Not Sup Chip：SDK 不支持的 PSE 芯片数量

### 5.12 Global PM Configuration Get CMD (0x4B) [App]

获取 PoE 子系统的系统功率管理模式和功率 bank 配置。

Request（表 5-23）：`CMD(0) | Bank ID(1) | RSVD(2-10) | CHKSM(11)` — 注意：**byte1 承载 Bank ID 而非 SEQ**
- Bank ID：0x00-0x07 = 有效 bank ID；0x08-0xFF = 请求失败

Response（表 5-24）：`CMD(0) | Bank ID(1) | PM Mode(2) | Bank ID Total Power(3-4) | Bank ID Reserved Power(5-6) | Bank ID+1 Total Power(7-8) | Bank ID+1 Reserved Power(9-10) | CHKSM(11)`
- Bank ID：0x00-0x07 = 有效 bank ID；0x08-0xFF = 请求失败
- PM Mode：0x00 = None；0x01 = Static 带优先级；0x02 = Dynamic 带优先级；0x03 = Static 不带优先级；0x04 = Dynamic 不带优先级
- Bank ID Total Power：Bank ID 的总功率（**0.1W/LSB**）
- Bank ID Reserved Power：Bank ID 的保留功率（**0.1W/LSB**）

### 5.13 Global Device Address Get CMD (0x4C) [App]

获取 PoE 控制器识别到的 PSE 芯片的 I2C 地址。

Request（表 5-25）：`CMD(0) | SEQ(1) | Idx(2) | RSVD(3-10) | CHKSM(11)`
- Idx：0x00-0x0B = 芯片索引（Chip_index）；0x0C-0xFF = 无效索引

Response（表 5-26）：`CMD(0) | SEQ(1) | Idx(2) | Addr Chip[idx](3) | Addr Chip[idx+1](4) | Addr Chip[idx+2](5) | Addr Chip[idx+3](6) | Addr Chip[idx+4](7) | Addr Chip[idx+5](8) | Addr Chip[idx+6](9) | Addr Chip[idx+7](10) | CHKSM(11)`
- Idx：0x00-0x0B = 芯片索引
- Offset+n：chip[idx+n] 的 I2C 低地址。**若 idx+n 位置无设备，该设备地址填 0xFF。**

### 5.14 Port Function Mode Get CMD (0x4D) [App]

获取 PSE 端口功能模式。PSE 功能模式可为：**semi-auto、auto 和 manual**。
- **Semi-auto**：端口自动完成检测与分级，但最终由主机 CPU 根据计算结果通知 PoE 控制器该 PD 是否可上电或返回 IDLE。
- **Auto**：端口自动完成检测与分级，PoE 控制器依据预设规则自行决定是否给该端口上电或返回 IDLE。
- **Manual**：端口需要主机 CPU 逐步触发来完成检测、分级和上电。

Request（表 5-27）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3) | Port(4) | RSVD(5) | Port(6) | RSVD(7) | Port(8) | RSVD(9) | RSVD(10) | CHKSM(11)` — 每次请求最多 4 个端口（字节 2/4/6/8）
- Port：0x00-0x2F = 有效逻辑端口索引（0-47）；0x30-0xFF = 无效

Response（表 5-28）：`CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效
- VAL：0x00 = Auto 模式；0x01 = Semi-auto 模式；0x02 = Manual 模式

### 5.15 Channel Status Get CMD (0x4E) [App]

获取指定端口的主通道和副通道状态。本命令可获取指定端口的基本通道信息：电源状态、故障状态、检测结果、分级结果和 PD 类型。

Request（表 5-29）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-30）：`CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)`
- STS1/STS5 — 主/副通道检测状态：0x0 = Unknown；0x1 = Short Circuit；0x2 = High Cap；0x3 = Rlow；0x4 = Valid PD；0x5 = Rhigh；0x6 = Open Circuit；0x7 = FET Failure；0x8-0xF = 保留
- STS2/STS6 — 主/副通道分级状态：0x0-0x8 = PD class 编号；0x9-0xB = 保留；0xC = PD 按 Class 0 处理；0xD = RSVD；0xE = Class Mismatch；0xF = Class over Current
- STS3/STS7 — 主/副通道故障状态：0x0 = OVLO；0x1 = MPS Absent；0x2 = Short；0x3 = Overload；0x4 = Power Denied；0x5 = Thermal Shutdown；0x6 = Inrush fail；0x7 = UVLO；0xE = GOTP
- STS4/STS8 — 主/副通道电源状态：0x0 = Disabled；0x1 = Searching；0x2 = Delivering Power；0x3 = RSVD；0x4 = Fault；0x5 = RSVD；0x6 = Requesting Power

### 5.16 Port Channel Voltage Current Get CMD (0x4F) [App]

获取属于指定端口的两个通道的电压和电流。

Request（表 5-31）：`CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port：0x00-0x2F 有效；0x30-0xFF 无效

Response（表 5-32）：`CMD(0) | SEQ(1) | Port(2) | Pri_Volt(3-4) | Pri_Curr(5-6) | Sec_Volt(7-8) | Sec_Curr(9-10) | CHKSM(11)` — _（文档标题误写为 "Port Mib Counter Get CMD"，为文档笔误）_
- Pri_Volt：主通道电压（**64.45mV/LSB**）
- Pri_Curr：主通道电流（**1mA/LSB**）
- Sec_Volt：副通道电压（**64.45mV/LSB**）
- Sec_Curr：副通道电流（**1mA/LSB**）

### 5.17 System Chip Type Information Get CMD (0x50) [App]

获取 PoE 子系统的所有 PSE 芯片类型信息。

Request（表 5-33）：`CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response（表 5-34）：`CMD(0) | SEQ(1) | STS1(2) | STS2(3) | STS3(4) | STS4(5) | STS5(6) | STS6(7) | RSVD(8-10) | CHKSM(11)`
- STS1：
  - BIT7-4 = chip0 的 PSE 类型：0x1 = 8 通道 bt 芯片；0x2 = 8 通道 at 芯片；0xE = 信息获取失败；0xF = 保留
  - BIT3-0 = chip1 的 PSE 类型：编码同上
- STS2-STS6：格式同 STS1 — STS2 的 BIT7-4 = chip2 的 PSE 类型，BIT3-0 = chip3，依此类推。本命令最多可显示 **12 颗**芯片的 PSE 类型信息。

## 6. 杂项命令 — 详解

### 6.1 Jump To Loader CMD (CMD-SUB ID=0xC0-00) [App]

从 App 区跳转到 Loader 区。用于固件升级：本命令使指针从 App 区跳转到 Loader 区。flash 中的固件信息将被**同时擦除** — 包括 App 镜像长度和校验和。

**本命令响应后，需等待一段时间再发送下一条命令。** 例如：当 MCU flash 大于 128KB（如 GD303）时，等待约 **8 秒**；当 MCU flash 为 64KB（如 GD230）时，等待约 **2 秒**。

Request（表 6-1）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x00）

Response（表 6-2）：`CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 6.2 Configuration Information Save CMD (0xC0-01) [App]

保存配置信息到 flash。信息包括系统参数和每端口参数。保存的配置可能影响下次启动项。

Request（表 6-3）：`CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- SUB：子命令（0x01）
- Year / Month / Day：配置的日期（**仅支持 RTL8238C/8239C**）
- High Version / Low Version：配置的版本（**仅支持 RTL8238C/8239C**）

Response（表 6-4）：`CMD(0) | SUB(1) | Save STS(2) | Version STS(3) | RSVD(4-10) | CHKSM(11)`
- Save STS：0x00 = 请求成功；0x01 = 请求失败
- Version STS（**仅支持 RTL8238C/8239C**）：0x00 = 请求成功；0x01 = 请求失败

### 6.3 Configuration Information Clear CMD (0xC0-02) [App]

从 flash 清除配置信息。

Request（表 6-5）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x02）

Response（表 6-6）：`CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 6.4 Configuration Version Save CMD (0xC0-03) [App]

保存配置版本信息到 ram 和 flash。信息包括配置的日期和版本。**仅支持 RTL8238B/8239。**

Request（表 6-7）：`CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- SUB：子命令（0x03）
- Year / Month / Day：配置的日期
- High Version / Low Version：配置的版本

Response（表 6-8）：`CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 6.5 Configuration Version Get CMD (0xC0-04) [App]

获取配置版本信息。信息包括配置的日期和版本。

Request（表 6-9）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x04）

Response（表 6-10）：`CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- Year / Month / Day：配置的日期
- High Version / Low Version：配置的版本

### 6.6 Configuration Information Reset CMD (0xC0-05) [App]

将配置信息复位为默认设置。

Request（表 6-11）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x05）

Response（表 6-12）：`CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 6.7 Configuration Information Initial CMD (0xC0-06) [App]

检查配置信息的有效性，判断是否需要复位配置信息。

Request（表 6-13）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x06）

Response（表 6-14）：`CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 6.8 App Download CMD (CMD-SUB ID=0xC0-80 ~ 83) [Loader only]

应用程序镜像可由主机 CPU 直接下载到 PoE 控制器 MCU。**仅当 PoE 控制器运行在 bootloader 时支持本命令。**

Request（表 6-15，最多 36 字节）：`CMD(0) | SUB(1) | Image Offset(2-3) | Data0(4) ... Data31(35)`
- SUB：0x80 = 0~64K 镜像块；0x81 = 64K~128K 镜像块；0x82 = 128K~192K 镜像块；0x83 = 192K~256K 镜像块
- Image Offset：0x0000-0xFFFF（最大 64KB），表示每个 64KB 帧在应用程序镜像中的偏移
- Data：镜像数据（**4~32 字节/帧，4 字节对齐**）

注意：
- **最后一帧 App Download 命令应只发送固件剩余的字节；禁止用 0xFF 或 0x0 填充到固定长度，否则会导致升级失败。**
- 若数据长度小于 32（如 4/8/12/16/20/24/28），命令之间应留有足够的延迟时间，以确保接收超时已结束。
- **在 Loader 代码中，只有 Loader 命令（App Download CMD、Jump To Loader CMD、Firmware download CMD）会得到响应；所有 App 命令都得不到任何响应。所有 Loader 命令及响应的长度不固定，且没有校验和字节。**
- **在 App 代码中，只要命令长度至少为 12 字节，无论 Loader 命令还是 App 命令都会得到响应。**

Response（表 6-16，4 字节）：`CMD(0) | SUB(1) | Image Offset(2-3)`
- SUB：0x80 = 0~64K；0x81 = 64K~128K；0x82 = 128K~192K；0x83 = 192K~256K 镜像块
- Image Offset：0x0000-0xFFFF（最大 64KB），每个 64KB 帧在应用程序镜像中的偏移

### 6.9 Jump To App CMD (CMD-SUB ID=0xC0-40) [Loader only]

从 Loader 区跳转到 App 区。本命令用于 App 镜像下载完成后执行校验和（CheckSum）例程。校验通过，PoE 控制器将自动跳转到 App；校验失败，App 区和外部 SPI Flash（如有必要）将被擦除，以备下次升级。
**本命令将在 1 秒内响应。响应返回前，不得发送任何命令。**

Request（表 6-17）：`CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB：子命令（0x40）

注意：
- **若 App 或 Firmware 镜像（如有需要）任一无效，App 和 Firmware 两个区域都将被擦除，然后等待下次升级。**
- Loader/App 代码响应规则：同 CMD 0xC0-80~83（Loader 代码只响应 Loader 命令且无校验和；App 代码响应所有 ≥12 字节的命令）。

Response（表 6-18）：`CMD(0) | SUB(1) | STS1(2) | STS2(3) | RSVD(4-10) | CHKSM(11)`
- STS1：0x00 = App 镜像有效性检查通过；0x01 = App 镜像无效
- STS2：0x00 = Firmware 镜像有效性检查通过；0x01 = Firmware 镜像无效

### 6.10 Firmware Download CMD (CMD ID=0xCA) [Loader only]

在 BT 项目中，部分小容量 flash 的 MCU 需要外部 SPI Flash。固件（Firmware）镜像可由主机 CPU 直接下载到外部 SPI Flash。**仅当 PoE 控制器运行在 bootloader 时支持本命令。**

Request（表 6-19，最多 36 字节）：`CMD(0) | Seq(1) | Image Offset(2-3) | Data0(4) ... Data31(35)`
- Image Offset：0x0000-0xFFFF（最大 64KB），表示本帧在应用程序镜像中的偏移
- Data：镜像数据（**支持 4/8/16/32 字节**）

注意：
- **早期 Loader 固件仅支持 32 字节数据长度**，且建议使用 32 字节以获得高效率。若需要 4/8/16 字节长度，请与 Realtek 确认是否需要发布新的 Loader 固件。
- 若数据长度小于 32（如 4/8/16），命令之间应留有足够的延迟时间，以确保接收超时已结束。
- Loader/App 代码响应规则：同 CMD 0xC0-80~83。

Response（表 6-20，4 字节）：`CMD(0) | Seq(1) | Image Offset(2-3)`
- Image Offset：0x0000-0xFFFF（最大 64KB），本帧在应用程序镜像中的偏移

## 7. 调试命令 — 详解

### 7.1 Chip Register Set CMD (0xF0) [App]

设置 PoE 芯片的寄存器值。

Request（表 7-1）：`CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | Register Value(7-10) | CHKSM(11)`
- ChipAddr：0x20 – 0x37 = 有效芯片 I2C 地址
- Register Address：32 位寄存器地址
- Register Value：32 位寄存器值

Response（表 7-2）：`CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | STS(7) | RSVD(8-10) | CHKSM(11)`
- STS：0x00 = 请求成功；0x01 = 请求失败

### 7.2 Chip Register Get CMD (0xF1) [App]

读取 PoE 芯片的寄存器值。

Request（表 7-3）：`CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | RSVD(7-10) | CHKSM(11)`
- ChipAddr：0x20 – 0x37 = 有效芯片 I2C 地址
- Register Address：32 位寄存器地址

Response（表 7-4）：`CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | Register Value(7-10) | CHKSM(11)`
- Register Value：32 位寄存器值

## 8. 修订说明（Rev.2.2 要点）

- 2.2：修改了 CMD-0xC、0xD、0x5、0x43、0xCA 和 0xC0 40 的描述。
- 2.1：CMD-0xE Byte 2（UVLO）/ Byte 4（OVLO）描述；CMD-0x44 Byte[7-8] 温度修正；CMD-0x4B 响应 Byte[2]=0x2；CMD-0xC0 02 Byte[1] 修正。
- 2.0：CMD-0x4A Byte[9] = SDK 不支持的 PSE IC 数量；CMD-0x13/14 AT/BT 建议最大功率；CMD-0x45 4KB RAM MCU 限制。
- 1.9：CMD-0x42/43/4E GOTP 与 inrush fail 状态；CMD-0x12/13/14 关系说明；CMD-0xC0 80、0xC0 40、0xCA 的注意事项。
- 1.8：CMD-0x5 端口映射开始/结束；CMD-0x42/43 GOTP 故障状态；CMD-0x19 short cable 功能；CMD-0x48 Byte[10] short cable 显示；CMD-0xC0 01 配置版本（RTL8238C/8239C）。