---
name: "rtl8239-host-command"
description: "RTL8239 PoE Controller host command (UART/I2C) protocol reference, based on Realtek Host Command Guide Rev.2.2. Invoke when developing, reviewing or debugging firmware code that communicates with RTL8239 (e.g. sending config commands 0x00-0x19, status get commands 0x40-0x50, firmware download 0xC0/0xCA, or debug register access 0xF0/0xF1)."
---

# RTL8239 PoE Controller Host Command Reference

Reference: Realtek RTL8239 PoE Controller Host Command Guide, Rev.2.2 (2025-08-29).

## 1. Overview

RTL8239 is a Power over Ethernet (PoE) PSE controller. The host MCU (e.g. HC32L021 in this project) communicates with it via UART or I2C using the command protocol described in this skill.

### N*8-port Management PSE Application Solution (Figure 1-1)

- The host CPU (switch CPU) communicates with the external CPU via **UART** (with isolation between them).
- The external CPU controls one or more RTL8239 PoE chips via the **I2C** interface.
- Each RTL8239 connects through UTP to PDs (Powered Devices).

```
HOST <--(Isolation)--> External CPU <--I2C--> RTL8239 #1..#N <--UTP--> PD
```

## 2. Communication Protocol

### 2.1 UART Interface (Table 2-1)

| Type | Value |
|------|-------|
| Baud Rate | 115200 |
| Stop bits | 1 |
| Parity | None |
| Flow Control | None |
| Inter Frame Gap | >20ms |
| Timeout | >50ms |
| Gap Between Bytes in the packet | <2ms |

- Normal commands respond within 50ms; **CMD 0x00 and 0x05 need more time**.

### 2.2 I2C Interface

- Max I2C rate: **1 MHz**.
- 7-bit device address of slave MCU: fixed **0x20**.
- Request = sequential write (Figure 2-1): `S | A6..A0 + W | ACK | Data0 | ACK | Data1 | ACK | ... | DataN | ACK | P`
- Response = sequential read (Figure 2-2): `S | A6..A0 + R | ACK | Data0 | ACK | Data1 | ACK | ... | DataN | NACK | P`
- Request and response frames are limited to **12-Byte payload** for App commands; for **App Download CMD** the max request payload is **36-Byte**.
- Inter Frame Gap between request and response frame recommended **> 20ms**, but CMD 0x00 and 0x05 need more time.

### 2.3 Protocol (Table 2-2 / 2-3)

Typical Request <-> Response sequence. The host CPU issues the request and waits for the external CPU's response within the predefined time. Most commands are **12 bytes**.

Request frame:

| Byte0 | Byte1 | Byte2 – Byte10 | Byte11 |
|-------|-------|----------------|--------|
| Command ID | Sequence Number | <=====Data=====> | Checksum |

Response frame:

| Byte0 | Byte1 | Byte2 – Byte10 | Byte11 |
|-------|-------|----------------|--------|
| Response ID | Sequence Number | <=====Data=====> | Checksum |

Checksum: cumulating all bytes from offset 0 to length-1, not considering the carry bit (i.e. 8-bit sum):

```
Checksum = (Byte0 + Byte1 + ... + Byte(len-1)) & 0xFF
```

Suggested sending/receiving CMD flow (Figure 2-1):

- **UART**: Send CMD → wait until enough response bytes received (count waiting time; on overflow → Return Error) → Check OK → Return Ok, else Return Error.
- **I2C**: Send CMD → **Wait 25ms** → Read CMD Response → Check Response → if Check NG, Try Times++ and retry until Try Max → Return Ok / Return Error.
- **Response check**: verify response **Byte[1] (Sequence Number)** and **Checksum**.

## 3. Command List

Commands are classified into four types: Configuration Set Commands, Configuration & Status Get Commands, Miscellaneous Commands, Debug Commands (Table 3-1).

### Configuration Set Commands

| CMD ID | Name | Description | Scope | Doc § |
|--------|------|-------------|-------|-------|
| 0x00 | Global Enable Set | Enable/disable all the ports. | App | 4.1 |
| 0x01 | Port Enable Set | Enable/disable the specified port. | App | 4.2 |
| 0x02 | Global Reset Set | Reset all the ports. | App | 4.3 |
| 0x03 | Port Reset Set | Reset the specified port. | App | 4.4 |
| 0x04 | Global Power Source Set | Set the power budget. | App | 4.5 |
| 0x05 | Port Mapping Enable Set | Enable/disable the port mapping. | App | 4.6 |
| 0x06 | Port Pair Mapping Set | Set the port mapping relationship. | App | 4.7 |
| 0x08 | Port Function Mode Set | Select the port function mode. | App | 4.8 |
| 0x09 | Port Detection Type Set | Set the port detection type. | App | 4.9 |
| 0x0A | Port Detection Trigger Set (Abandoned, only manual mode) | Force the port to do the detection one time. | App | 4.10 |
| 0x0B | Port Class Trigger Set | Force the port to do the classification once. | App | 4.11 |
| 0x0C | Port Inrush Mode Set | Set the port inrush limit. | App | 4.12 |
| 0x0D | Port Force Inrush Set | Another way to set the inrush/power on limit. | App | 4.13 |
| 0x0E | Global Parameters Set | Set the UVLO/OVLO threshold. | App | 4.14 |
| 0x0F | Port Disconnect Type Set | Set disconnect type with delay or not. | App | 4.15 |
| 0x10 | Global Power Management Mode Set | Set the power management mode. | App | 4.16 |
| 0x11 | Global Power Management Mode Extended Set | Set system pre-allocated function. | App | 4.17 |
| 0x12 | Port Max Power Type Set | Set the port max power threshold type. | App | 4.18 |
| 0x13 | Port Max Power Value Set (Max 51W) | Set the port max power threshold value, unit **0.2W/LSB**. | App | 4.19 |
| 0x14 | Port Max Power Value Extended Set (Max 102W) | Set the port max power threshold value, unit **0.4W/LSB**. | App | 4.20 |
| 0x15 | Port Priority Set | Set the port priority. | App | 4.21 |
| 0x16 | Global Port Event Mask Set | Set the port event mask. | App | 4.22 |
| 0x18 | Port Trigger Det CLS PWR Set (Only Manual Mode) | Force port power. | App | 4.23 |
| 0x19 | Port Cable Type Set (Only 4Pair Mode RTL8239C) | _(in TOC; not listed in Table 3-1)_ | App | 4.24 |

### Configuration and Status Get Commands

| CMD ID | Name | Description | Scope | Doc § |
|--------|------|-------------|-------|-------|
| 0x40 | Global Status Get | Get the global status. | App | 5.1 |
| 0x41 | Global Power Status Get | Get the power status. | App | 5.2 |
| 0x42 | Port Status Get | Get the detailed status for the specified port. | App | 5.3 |
| 0x43 | Port Group Status Get | Get the basic status for the port group. | App | 5.4 |
| 0x44 | Port Measurement Get | Get port voltage/current/power; get IC central temperature. | App | 5.5 |
| 0x45 | Port Mib Counter Get | Get the port mib counter information. | App | 5.6 |
| 0x46 | Port Event Status Get | Get the port event status. | App | 5.7 |
| 0x47 | Global Reset Reason Get | Get the global reset reason. | App | 5.8 |
| 0x48 | Port Basic Configuration Get | Get the port basic information (auto/semi, etc). | App | 5.9 |
| 0x49 | Port Extended Configuration Get | Get the port extended information. | App | 5.10 |
| 0x4A | Global Parameters Get | Get the global parameters. | App | 5.11 |
| 0x4B | Global PM Configuration Get | Get the power management configuration. | App | 5.12 |
| 0x4C | Global Device Address Get | Get the device address. | App | 5.13 |
| 0x4D | Port Function Mode Get | Get the port detection type (Table 3-1 names it "Port Detection Type Get"). | App | 5.14 |
| 0x4E | Channel Status Get | Get the basic status of two channels. | App | 5.15 |
| 0x4F | Port Channel Voltage Current Get | Get the port's two channel voltage/current. | App | 5.16 |
| 0x50 | System Chip Type Information Get | Get all the PSE chip type informations of PoE subsystem. | App | 5.17 |

### Miscellaneous Commands

| CMD ID / SUB ID | Name | Description | Scope | Doc § |
|-----------------|------|-------------|-------|-------|
| 0xC0-00 | Jump To Loader | Sub command(00). | App | 6.1 |
| 0xC0-01 | Configuration Information Save | Sub command(01). | App | 6.2 |
| 0xC0-02 | Configuration Information Clear | Sub command(02). | App | 6.3 |
| 0xC0-03 | Configuration Version Save (only RTL8238B/8239) | Sub command(03). | App | 6.4 |
| 0xC0-04 | Configuration Version Get | Sub command(04). | App | 6.5 |
| 0xC0-05 | Configuration Information Reset | Sub command(05). | App | 6.6 |
| 0xC0-06 | Configuration Information Initial | Sub command(06) (Table 3-1 labels it "Save", likely a doc typo). | App | 6.7 |
| 0xC0-80 ~ 0xC0-83 | App Download | Sub command(0x80~83), only used in loader. | **Loader** | 6.8 |
| 0xC0-40 | Jump To App | Sub command(0x40), only used in loader. | **Loader** | 6.9 |
| 0xCA | Firmware Download | Download firmware to extern flash. | **Loader** | 6.10 |

### Debug Commands

| CMD ID | Name | Description | Scope | Doc § |
|--------|------|-------------|-------|-------|
| 0xF0 | Chip Register Set | Set the specified register directly. | App | 7.1 |
| 0xF1 | Chip Register Get | Get the specified register directly. | App | 7.2 |

## 4. Configuration Set Commands — Details

> **General note**: The default value of unused bytes in CMD used in **APP** needs to be **0xFF** (applies to all commands).
> STS field common values: 0x00 = Request success; 0x01 = Request failed.
> Logical port index: 0x00-0x2F valid (port 0-47); 0x30-0xFF invalid.

### 4.1 Global Enable Set CMD (0x00) [App]

Enable or disable all the ports. Disable: all ports remain idle without detection & classification. Enable: all ports do detection & classification normally and get the chance to power up. **Response within 1 second; no command should be sent before the response.**

Request (Table 4-1): `CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL: 0x00 = Disable all the ports function; 0x01 = Enable all the ports function; 0x02-0xFF = RSVD

Response (Table 4-2): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.2 Port Enable Set CMD (0x01) [App]

Enable or disable single port.

Request (Table 4-3): `CMD(0) | SEQ(1) | Port(2) | VAL(3) | Port(4) | VAL(5) | Port(6) | VAL(7) | Port(8) | VAL(9) | RSVD(10) | CHKSM(11)` — up to 4 Port/VAL pairs
- VAL: 0x00 = Disable the port function; 0x01 = Enable the port function; **0x02 = Force the port power up in semi-auto mode**; 0x03-0xFF = RSVD

Response (Table 4-4): `CMD(0) | SEQ(1) | Port(2) | STS(3) | Port(4) | STS(5) | Port(6) | STS(7) | Port(8) | STS(9) | RSVD(10) | CHKSM(11)` — 4 Port/STS pairs

### 4.3 Global Reset Set CMD (0x02) [App]

Reset the whole PoE subsystem.

Request (Table 4-5): `CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL: 0x00 = Not to reset PoE system; 0x01 = Reset whole PoE system

Response (Table 4-6): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.4 Port Reset Set CMD (0x03) [App]

Reset port state machine to idle and configuration value to default.

Request (Table 4-7): `CMD(0) | SEQ(1) | Port(2) | VAL(3) | Port(4) | VAL(5) | Port(6) | VAL(7) | Port(8) | VAL(9) | RSVD(10) | CHKSM(11)` — up to 4 Port/VAL pairs
- VAL: 0x00 = Not to reset port; 0x01 = Reset port

Response (Table 4-8): `CMD(0) | SEQ(1) | Port(2) | STS(3) | Port(4) | STS(5) | Port(6) | STS(7) | Port(8) | STS(9) | RSVD(10) | CHKSM(11)`

### 4.5 Global Power Source Set CMD (0x04) [App]

Set system power bank and reserved power value of the specific bank. **Reserved Power must be less than Total Power.**

Request (Table 4-9): `CMD(0) | SEQ(1) | Bank ID(2) | Total Power(3-4) | Reserved Power(5-6) | RSVD(7-10) | CHKSM(11)`
- Bank ID: 0x00-0x07 = valid bank id; 0x08-0xFF = invalid (request failed)
- Total Power: unit **0.1W/LSB**
- Reserved Power: unit **0.1W/LSB**

Response (Table 4-10): `CMD(0) | SEQ(1) | Bank ID(2) | STS(3) | RSVD(4-10) | CHKSM(11)`
- Bank ID: 0x00-0x07 = valid; 0x08-0xFF = invalid

### 4.6 Port Mapping Enable Set CMD (0x05) [App]

Set system logical-to-physical port mapping enable status. This command should be issued **before and after** port mapping. **Response within 2 seconds; no command should be sent before the response.**
- At the beginning of port mapping (VAL=0x00): all runtime POE configurations will be **reset**.
- At the end of port mapping (VAL=0x01): all runtime POE configurations including port mapping will be **saved in MCU flash**.

Request (Table 4-11): `CMD(0) | SEQ(1) | VAL(2) | MaxPort(3) | RSVD(4-10) | CHKSM(11)`
- VAL: 0x00 = Disable port mapping, port mapping start; 0x01 = Enable port mapping, port mapping stop
- Max Port: 0x00-0x30 = valid port number (0-48); 0x31-0xFF = invalid

Notes:
- Port mapping will **restore the PoE configuration to the default value**, so the configuration before port mapping will be effective.
- Port mapping must be set to disable (VAL=0x0) using CMD-0x5 at the beginning, and then set to enable (VAL=0x1) using CMD-0x5 at the end.

Response (Table 4-12): `CMD(0) | SEQ(1) | VALSTS(2) | MaxPort STS(3) | RSVD(4-10) | CHKSM(11)`
- VALSTS: 0x00 = success; 0x01 = failed
- MaxPortSTS: 0x00 = success; 0x01 = failed

### 4.7 Port Pair Mapping Set CMD (0x06) [App]

Configure device index and channel index for the specific logical port. All chip I2C addresses should be in range **0x20 to 0x3E, even number**.
- If board I2C address starts from 0x20 and all addresses are contiguous: byte[10] = **0xFF**, chip index = (chip I2C address − 0x20) / 2.
- If board address doesn't start from 0x20 or addresses are discontinuous: byte[10] = chip I2C address, and chip index is suggested to be a smaller value for the smaller I2C address.

Request (Table 4-13): `CMD(0) | SEQ(1) | Port(2) | 4-Pair Enable(3) | Chip index(4) | Pri-Channel(5) | Sec-Channel(6) | RSVD(7-9) | Addr(10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid
- 4-Pair Enable: 0x00 = 2-pair mode; 0x01 = 4-pair mode
- Chip index (DEVICE ID): 0x0-0xE
- Pri-Channel: 0x0-0x7 = Primary channel of 4-pair port in 4-pair mode, or channel index in 2-pair mode
- Sec-Channel: 0x0-0x7 = Secondary channel of 4-pair port in 4-pair mode
- Addr: chip I2C address (only used when board address doesn't start from 0x20 or discontinuous; otherwise 0xFF)

Notes:
- **AT application**: Byte[3] 4-Pair Enable = 0x00 (2-pair), Byte[6] Sec Channel = 0xFF.
- **BT application**: Byte[3] = 0x01 (4-pair), Byte[5-6] set to the corresponding channels.
- Port mapping will restore the PoE configuration to the default value, so the configuration before port mapping will be effective.

Response (Table 4-14): same byte layout as request; each field echoes a per-field status:
- Port: 0x00-0x2F valid; 0x30-0xFF invalid
- 4-Pair Enable / Chip ID / Pri-Channel / Sec-Channel: 0x00 = success; 0x01 = failed
- Addr: if board address doesn't start from 0x20 or discontinuous → 0x00 = success, 0xFF = failed; if addresses start from 0x20 and contiguous → 0xFF = not care

### 4.8 Port Function Mode Set CMD (0x08) [App]

Set PSE port function mode: **semi-auto / auto / manual**.
- **Semi-auto**: port does detection & classification automatically, but finally the host CPU informs the PoE controller whether the PD can be powered on or return to IDLE according to the calculation.
- **Auto**: port does detection & classification automatically, and the PoE controller decides whether to power on or return to IDLE per predetermined rules.
- **Manual**: the port can be powered up forcefully through CMD-0x18 (Port Trigger Det CLS PWR).

Request (Table 4-15): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Auto mode; 0x01 = Semi-auto mode; 0x02 = Manual mode

Response (Table 4-16): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.9 Port Detection Type Set CMD (0x09) [App]

Decides whether to do classification for Legacy PD and whether the classification-failed PD can be powered on. **When doing Sifos test, VAL should be set to 0/2/4** — other values will cause **det_range and det_cc failure**.

Request (Table 4-17): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL = 0x00, 0x02, 0x04: do classification for standard but **not** for Legacy PD; classification-failed PD **cannot** be powered on (0x0/0x2/0x4: no difference in functions)
- VAL = 0x01, 0x03, 0x05: do classification for standard **and** Legacy PD; classification-failed PD **cannot** be powered on (0x1/0x3/0x5: no difference in functions)
- VAL = 0x06: do classification for standard **and** Legacy PD; classification-failed PD **can** be powered on

Response (Table 4-18): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.10 Port Detection Trigger Set CMD (0x0A, Abandoned — only manual mode) [App]

Force the specific port to do detection in manual mode and get detection PD type.

Request (Table 4-19): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- **SEQ: 0x00 = Set command; 0x01 = Get command** (SEQ doubles as set/get selector)
- VAL (Set command): 0x00 = Not trigger; 0x01 = Trigger
- VAL (Get command): set to 0xFF

Response (Table 4-20): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- STS (Set command): 0x00 = success; 0x01 = failed; 0xFF = invalid value
- STS (Get command): 0x00 = Valid PD detected; 0x01 = Invalid PD detected; 0xFF = invalid value

### 4.11 Port Class Trigger Set CMD (0x0B) [App]

Force the specific port to do classification in auto mode and semi-auto mode.

Request (Table 4-21): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = None; 0x01 = Force class enable

Note: **This CMD will cause Sifos det_range fail — do not use it when testing Sifos.**

Response (Table 4-22): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.12 Port Inrush Mode Set CMD (0x0C) [App]

Set the inrush mode of the port to configure the maximum PD class level that the port can support.

Request (Table 4-23): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`

| VAL | Mode | Max PD class | Max Iinrush | Max Ilim |
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

Note: **When testing AF/AT Sifos, inrush mode should be set to 0x00 (802.3af) or 0x03 (802.3at). BT inrush mode will cause AF/AT Sifos det_time fail.**

Response (Table 4-24): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.13 Port Force Inrush Set CMD (0x0D) [App]

Set whether the inrush of a single port or multiple ports increases. After enabling, the inrush increases by one level. Inrush levels: **212.5mA, 425mA, 850mA, 1275mA**.
**This CMD is ineffective for inrush mode 0x01 (802.3af High Inrush), 0x04 (Pre-802.3bt type3), 0x07 (Pre-802.3bt type4).**

Request (Table 4-25): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Force Inrush Disable; 0x01 = Force Inrush Enable

Note: **When testing Sifos, set VAL to 0x00 (Force Inrush Disable).**

Response (Table 4-26): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.14 Global Parameters Set CMD (0x0E) [App]

Set system parameters of PoE subsystem: UVLO threshold and OVLO threshold.

Request (Table 4-27): `CMD(0) | SEQ(1) | UVLO(2) | RSVD(3) | OVLO(4) | RSVD(5-10) | CHKSM(11)`
- UVLO: **33V + UVLO × 64.45mV/LSB**
- OVLO: **57V + OVLO × 64.45mV/LSB**, max 60V → 0x00-0x2F valid; 0x30-0xFF invalid

Notes:
- If either OVLO or UVLO is the invalid value **0xFF, neither will take effect**.
- If OVLO > 0x30 and ≠ 0xFF, firmware defaults it to the max value **0x2F**.
- Due to conversion, the OVLO/UVLO values retrieved by CMD-0x4A may be **decreased by 1**.

Response (Table 4-28): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.15 Port Disconnect Type Set CMD (0x0F) [App]

Set disconnect type of the specific port or multiple ports.

Request (Table 4-29): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Disable MPS ability; 0x01 = Reserved; 0x02 = Enable MPS ability; 0x03 = Enable MPS ability (**MPS function will be enabled after 700ms of pwrup**)

Note: When disconnect type = 0x00 (Disable MPS), **port will not turn off due to low current**.

Response (Table 4-30): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.16 Global Power Management Mode Set CMD (0x10) [App]

Set global power management mode of PoE subsystem.
- **Static mode**: power management based on port **class** power consumption.
- **Dynamic mode**: power management based on the **actual** power consumption of the port.
- **Priority mode**: high-priority ports can preempt low-priority ports to complete power-on; no preemption relationship with the same priority. Port priority is set by CMD-0x15.

Request (Table 4-31): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)` — byte2 field named STS but carries the mode value:
- 0x00 = None; 0x01 = Static mode with priority; 0x02 = Dynamic mode with priority; 0x03 = Static mode without priority; 0x04 = Dynamic mode without priority

Response (Table 4-32): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)` — STS 0x00 = success; 0x01 = failed

### 4.17 Global Power Management Mode Extended Set CMD (0x11) [App]

Set extended global power management mode: system pre-allocated function and current mode margin function.
- Pre-allocated **enabled**: if a specific port can be powered up, system remain power must be larger than port request power.
- Pre-allocated **disabled**: port request power will not be necessary to power up this port.

Request (Table 4-33): `CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL: 0x00 = Enable system pre-allocated function; 0x01-0xFF = Disable system pre-allocated function

Response (Table 4-34): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.18 Port Max Power Type Set CMD (0x12) [App]

Set single-port or multi-port maximum power type.
- **Class based**: max power set according to the class level; port max power has nothing to do with CMD-0x13/0x14. **Class mode should be used when doing Sifos test.**
- **User defined**: max power set by CMD-0x13 or CMD-0x14.

Request (Table 4-35): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Reserved; 0x01 = Class based; 0x02 = User defined

Note: **When testing Sifos, set VAL to 0x01.**

Response (Table 4-36): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.19 Port Max Power Value Set CMD (0x13, Max 51W) [App]

Set max power value of the specific port or multiple ports. Maximum value = 0.2W × 255 = **51W**.
**Only takes effect when the port is in user defined mode** (CMD-0x12 VAL=0x02); in class mode it has no effect.
Recommended: AT max power < 36W; BT max power < 98W.

Request (Table 4-37): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: **0.2W/LSB**

Response (Table 4-38): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

### 4.20 Port Max Power Value Extended Set CMD (0x14, Max 102W) [App]

Set max power value of the specific port or multiple ports. Maximum value = 0.4W × 255 = **102W**.
**Only takes effect when the port is in user define mode** (CMD-0x12 VAL=0x02); in class mode, port maximum power has nothing to do with CMD-0x13 and CMD-0x14.
Recommended: AT max power < 36W; BT max power < 98W.

Request (Table 4-39): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: **0.4W/LSB**

Response (Table 4-40): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

### 4.21 Port Priority Set CMD (0x15) [App]

Set assign priority of the specific port or multiple ports.

Request (Table 4-41): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Low; 0x01 = Medium; 0x02 = High; 0x03 = Critical

Response (Table 4-42): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

### 4.22 Global Port Event Mask Set CMD (0x16) [App]

Set several kinds of global port event mask. Before event status can be recorded and interrupt pulse can be transmitted to host, the corresponding mask must be set. Fault events include **short, OVLO, UVLO and overload**.

Request (Table 4-43): `CMD(0) | SEQ(1) | VAL(2) | RSVD(3-10) | CHKSM(11)`
- VAL bitmap:
  - BIT0: Reserved
  - BIT1: Disconnect events mask (0 = DISABLE, 1 = ENABLE)
  - BIT2: Fault events mask: short, OVLO, UVLO and overload (0 = DISABLE, 1 = ENABLE)
  - BIT3-7: Reserved

Response (Table 4-44): `CMD(0) | SEQ(1) | STS(2) | RSVD(3-10) | CHKSM(11)`

### 4.23 Port Trigger Det CLS PWR CMD (0x18, Only Manual Mode) [App]

Force the specific port to do power in manual mode.

Request (Table 4-45): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Port Reset; 0x01 = Reserve; 0x02 = Reserve; 0x03 = Force power enable

Response (Table 4-46): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

### 4.24 Port Cable Type Set CMD (0x19, Only 4Pair Mode RTL8239C) [App]

Set cable type in 4Pair Mode. Two channels of **Normal Type** correspond to 4Pair; two channels of **Short Cable Type** correspond to 2Pair.

Request (Table 4-47): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- VAL: 0x00 = Normal Cable; 0x01 = Short Cable

Response (Table 4-48): `CMD(0) | SEQ(1) | (Port,STS)×4 (2-9) | RSVD(10) | CHKSM(11)`

## 5. Configuration and Status Get Commands — Details

### 5.1 Global Status Get CMD (0x40) [App]

Get basic system status of PoE subsystem: PoE mode, communication interface, max ports, port map, device ID, software version, MCU type, configuration status and extended version.

Request (Table 5-1): `CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response (Table 5-2): `CMD(0) | SEQ(1) | RSVD(2) | Max Ports(3) | Port Map(4) | Device ID(5-6) | SW Ver(7) | MCU Type(8) | Config Status(9) | Ext.Ver(10) | CHKSM(11)`
- Max Ports: the Max Ports in PoE system
- Port Map: system port enable status — 0x00 = Disable; 0x01 = Enable
- Device ID: 16-bit — 0x0138 = RTL8238B; 0x0238 = RTL8238C; 0x0039 = RTL8239; 0x0139 = RTL8239C
- SW Ver: 8-bit Software Version — BIT7-4 = Major Number; BIT3-0 = Minor Number
- MCU Type: 8-bit — 0x00 = GigaDevice GD32F310XXXX; 0x01 = GD32E230XXXX; 0x02 = GD32F303XXXX; 0x03 = GD32F103XXXX; 0x04 = GD32E103XXXX; 0x10 = Nuvoton M0516XXXX; 0x11 = Nuvoton M0564XXXX; 0x12 = Nuvoton NUC029XXXX
- Config Status bitmap:
  - BIT0: Configuration Status bit — 0x0 = Configuration is dirty; 0x1 = Configuration is saved
  - BIT1: System Reset bit — 0x0 = NO System Reset happened; 0x1 = System Reset happened
  - BIT2: Global Disable Pin Indication — 0x0 = Global Disable Pin is low; 0x1 = Global Disable Pin is high
- Ext.Ver: 8-bit Extender Software Version — BIT7-4 = Major Number; BIT3-0 = Minor Number

### 5.2 Global Power Status Get CMD (0x41) [App]

Get system power status of PoE subsystem: system allocated power, system available power, power bank id and system current power.
- **Static mode**: System Available Power = System Total Power − System Allocated Power
- **Dynamic mode**: System Available Power = System Total Power − System Current Power

Request (Table 5-3): `CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response (Table 5-4): `CMD(0) | SEQ(1) | System Allocated Power(2-3) | System Available Power(4-5) | Bank ID(6) | System Current Power(7-8) | RSVD(9-10) | CHKSM(11)`
- System Allocated Power: system total power allocated by port class level (**0.1W/LSB**)
- System Available Power: system available power of the current bank id (**0.1W/LSB**)
- Bank ID: bank ID used by the system
- System Current Power: system total power actually consumed by port (**0.1W/LSB**)

### 5.3 Port Status Get CMD (0x42) [App]

Get basic information of the specified port: power state, fault status, detection result, classification result and PD type.

Request (Table 5-5): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-6): `CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)`
- STS1 — Port power status or fault status: 0x00 = Disabled; 0x01 = Searching; 0x02 = Delivering Power; 0x03 = RSVD; 0x04 = Fault; 0x05 = RSVD; 0x06 = Requesting Power
- STS2 — **If STS1 shows Fault or Other Fault, STS2 shows Error Types**: 0x00 = OVLO; 0x01 = MPS Absent; 0x02 = Short; 0x03 = Overload; 0x04 = Power Denied; 0x05 = Thermal Shutdown; 0x06 = Inrush fail; 0x07 = UVLO; 0x0E = GOTP.
  **Else STS2 shows Detection and Classification result**:
  - BIT7-4 Classification result: 0x0-0x8 = PD class numbers; 0x9-0xB = Reserved; 0xC = PD treated as Class 0; 0xD = RSVD; 0xE = Class Mismatch; 0xF = Class over Current
  - BIT3-0 Detection Result: 0x0 = Unknown; 0x1 = Short Circuit; 0x2 = High Cap; 0x3 = Rlow; 0x4 = Valid PD; 0x5 = Rhigh; 0x6 = Open Circuit; 0x7 = FET Failure; 0x8-0xF = Reserved
- STS3 — If connection check is dual: BIT7-4 = Sec-Channel classification result, BIT3-0 = Pri-Channel classification result; Else BIT7-0 = valid channel classification result
- STS4: RSVD
- STS5 — Connection check result: 0x00 = 2pair; 0x01 = Single PD; 0x02 = Dual PD; 0x03 = Unknown
- STS6 / STS7 / STS8: RSVD

Notes:
1. STS2 indicates fault types instead of detection/classification result if port status (STS1) is fault status.
2. When a dual PD is connected, STS1 will indicate Requesting Power status when any channel meets power requirements; otherwise STS1 will indicate Delivering Power status when any channel has been powered on.
3. When a dual PD is connected and no fault event happens, Bit3-0 of STS2 will indicate legacy detection result when any channel has detected a legacy PD.
4. When a dual PD is connected, Bit3-0 of STS3 indicates classification result of primary channel, Bit7-4 indicates classification result of secondary channel.

### 5.4 Port Group Status Get CMD (0x43) [App]

Get one group of ports status at one time. All ports are split into groups of **4 ports** (e.g. 8-port system: Group 0 = port 0-3, Group 1 = port 4-7). Information per port: power state, fault status, detection result, classification result and PD type.

Request (Table 5-7): `CMD(0) | SEQ(1) | Group(2) | RSVD(3-10) | CHKSM(11)`
- Group: 0x00-0x0B = valid group index (0-11); 0x0C-0xFF = invalid

Response (Table 5-8): `CMD(0) | SEQ(1) | Group(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)` — 4 ports per group: (STS1,STS2) = 1st port, (STS3,STS4) = 2nd, (STS5,STS6) = 3rd, (STS7,STS8) = 4th
- STS1/STS3/STS5/STS7:
  - BIT7-4 Detection Result: 0x0 = Unknown; 0x1 = Short Circuit; 0x2 = High Cap; 0x3 = Rlow; 0x4 = Valid PD; 0x5 = Rhigh; 0x6 = Open Circuit; 0x7 = FET Failure; 0x8-0xF = Reserved
  - BIT3-0 Port power status or fault status: 0x0 = Disabled; 0x1 = Searching; 0x2 = Delivering Power; 0x3 = RSVD; 0x4 = Fault; 0x5 = RSVD; 0x6 = Requesting Power
- STS2/STS4/STS6/STS8:
  - BIT7-4 Error Types (effective when port power status or fault status = Fault): 0x0 = OVLO; 0x1 = MPS Absent; 0x2 = Short; 0x3 = Overload; 0x4 = Power Denied; 0x5 = Thermal Shutdown; 0x6 = Inrush fail; 0x7 = UVLO; 0xE = GOTP
  - BIT3-0 Classification result (if connection check is dual, the value is the **sum of two channel classification results**): 0x0-0x8 = PD class numbers; 0x9-0xB = Reserved; 0xC = PD treated as Class 0; 0xD = RSVD; 0xE = Class Mismatch; 0xF = Class over Current

Notes:
1. When a dual PD is connected, Bit3-0 of STS1/STS3/STS5/STS7 will indicate Requesting Power status when any channel meets power requirements; otherwise Bit3-0 will indicate Delivering Power status when any channel has been powered on.
2. When a dual PD is connected, Bit7-4 of STS1/STS3/STS5/STS7 will indicate legacy detection result when any channel has detected a legacy PD.
3. When a dual PD is connected, Bit3-0 of STS2/STS4/STS6/STS8 will indicate the sum of primary channel and secondary channel classification result.

### 5.5 Port Measurement Get CMD (0x44) [App]

Get the specified port measurements: port voltage, current, temperature, and actual consumption power.

Request (Table 5-9): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-10): `CMD(0) | SEQ(1) | Port(2) | Voltage(3-4) | Current(5-6) | Temperature(7-8) | Power(9-10) | CHKSM(11)`
- Voltage: Port Voltage (**64.45mV/LSB**)
- Current: Port Current (**1mA/LSB**)
- Temperature: IC central temperature = **(Temperature − 120) × (−1.25) + 125**
- Power: Port Actual Consumption Power (**0.1W/LSB**)

### 5.6 Port Mib Counter Get CMD (0x45) [App]

Get port mib counters of the specified port: MPS absent Counter, Overload Counter, Short Counter, Power Denied Counter and Invalid Signature Counter.
**When the MCU SRAM is less than 4K (e.g. NUC029 and M0516), it may not support the mib counter function.**

Request (Table 5-11): `CMD(0) | SEQ(1) | Port(2) | Reset Flag(3) | RSVD(4-10) | CHKSM(11)`
- Reset Flag: 0x00 = Don't reset mib counter after read; 0x01 = Reset mib counter after read

Response (Table 5-12): `CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | RSVD(8-10) | CHKSM(11)`
- STS1 = MPS absent Counter; STS2 = Overload Counter; STS3 = Short Counter; STS4 = Power Denied Counter; STS5 = Invalid Signature Counter

### 5.7 Port Event Status Get CMD (0x46) [App]

Get event status of all ports: event mask configuration, system event status and all ports event status.

Request (Table 5-13): `CMD(0) | SEQ(1) | Clear Flag(2) | RSVD(3-10) | CHKSM(11)`
- Clear Flag: 0x00 = Don't clear port event status after read; 0x01 = Clear port event status after read

Response (Table 5-14): `CMD(0) | SEQ(1) | Event Mask(2) | Event Status(3) | STS1[7-0](4) | STS2[15-8](5) | STS3[23-16](6) | STS4[31-24](7) | STS5[39-32](8) | STS6[47-40](9) | RSVD(10) | CHKSM(11)`
- Event Mask (global mask; must be set before event status can be recorded and interrupt pulse transmitted to Host):
  - BIT0: RSVD; BIT1: Disconnect events mask (0 = DISABLE, 1 = ENABLE); BIT2: Fault events mask (0 = DISABLE, 1 = ENABLE); BIT3-7: Reserved
- Event Status (for each global event status, if this kind event status of any port happened, the corresponding bit will be set):
  - BIT0: RSVD; BIT1: Disconnect events status (0 = not happened, 1 = happened); BIT2: Fault events status (0 = not happened, 1 = happened); BIT3-7: Reserved
- STS1..STS6 = 48-bit port event bitmap: STS1[7-0] shows event status of port7 to port0, STS2[15-8] shows port15 to port8, and so on. E.g. STS1[0] = port0 event status — if any kind of port0 event status is recorded, STS1[0] will be set; STS1[7] = port7 event status.

### 5.8 Global Reset Reason Get CMD (0x47) [App]

Get system reset reason. This command can get the I2C address of reset or abnormal chip.

Request (Table 5-15): `CMD(0) | SEQ(1) | Clear Flag(2) | RSVD(3-10) | CHKSM(11)`
- Clear Flag: 0x00 = Don't clear Reset reason after read; 0x01-0xFF = Clear Reset reason after read

Response (Table 5-16): `CMD(0) | SEQ(1) | Reset Flag(2) | Error Addr(3) | Reset reason(4) | Error Addr 01(5) | Error Addr 23(6) | Error Addr 45(7) | Error Addr 67(8) | Error Addr 89(9) | Error Addr 10 11(10) | CHKSM(11)`
- Reset Flag: if all chip I2C interfaces are ok, this byte = **0x00**; else **bit[5-0]** = the first I2C address, **bit[6]** reflects chip reset status
- Error Addr: if all chip I2C interfaces are ok, this byte = **0xFF**; else this byte reflects the I2C first address error
- Reset reason: 0x01 = power on reset; 0x02 = nRST reset; 0x03 = software reset; 0x04 = other error reset
- Error Addr 01:
  - BIT7-4: chip0 access error flag — 0xF = chip0 access normally; 0x0-0xE = equivalent value of chip0 I2C address (**chip I2C address = equivalent value × 2 + 0x20**)
  - BIT3-0: chip1 access error flag — same encoding as above
  - Error Addr 23 and others are similar to this one (each byte covers 2 chips)

### 5.9 Port Basic Configuration Get CMD (0x48) [App]

Get basic configuration of the specified port: enable status, function mode, detection type, classification type, disconnect type, and pair type.

Request (Table 5-17): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-18): `CMD(0) | SEQ(1) | Port(2) | Enable Status(3) | Function Mode(4) | Det Type(5) | Cls Type(6) | DISCXNT Type(7) | Pair Type(8) | RSVD(9) | Cable Type(10) | CHKSM(11)`
- Enable Status: 0x00 = Disabled; 0x01 = Enabled
- Function Mode: 0x00 = Auto mode; 0x01 = Semi-auto mode; 0x02 = Manual mode
- Det Type (Detection Type): 0x00/0x02/0x04 = do class for std PD; 0x01/0x03/0x05 = do class for std and legacy PD; 0x06 = do class for overCurrent PD
- Cls Type: RSVD
- Disconnect Type: 0x00 = Disable MPS ability; 0x01 = Reserved; 0x02 = Enable MPS ability; 0x03 = Enable MPS ability (**MPS function will be enabled after 700ms of pwrup**)
- Pair Type: 0x00 = Alternative A; 0x01 = Alternative B
- Cable Type (4Pair mode): 0x00 = Normal; 0x01 = Short

### 5.10 Port Extended Configuration Get CMD (0x49) [App]

Get extended configuration of the specified port: PD inrush mode, power limit mode, power threshold, priority and port map array.

Request (Table 5-19): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-20): `CMD(0) | SEQ(1) | Port(2) | Inrush Mode(3) | Limit Type(4) | Max Power(5) | Priority(6) | ChipAddr(7) | Pri-Chnl(8) | Sec-Chnl(9) | RSVD(10) | CHKSM(11)`
- Inrush Mode: 0x00 = IEEE 802.3af; 0x01 = IEEE 802.3af high inrush; 0x02 = IEEE 802.3at compatible; 0x03 = IEEE 802.3at; 0x04 = Pre-IEEE 802.3bt type3 mode; 0x05 = IEEE 802.3bt type3 mode; 0x06 = IEEE 802.3bt type4 mode; 0x07 = Pre-IEEE 802.3bt type4 mode; 0x09 = IEEE 802.3at ALT B mode
- Limit Type: 0x01 = Class based; 0x02 = User defined
- Max Power: Port power max threshold (**0.4W/LSB**)
- Priority: 0x00 = Low; 0x01 = Medium; 0x02 = High; 0x03 = Critical
- ChipAddr: chip I2C address
- Pri-Chnl: Primary channel; Sec-Chnl: Secondary channel

### 5.11 Global Parameters Get CMD (0x4A) [App]

Get extended system configuration: UVLO threshold, pre-allocated status, power up mode, disconnect behavior, detection flag, OVLO threshold and PSE chip number.

Request (Table 5-21): `CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response (Table 5-22): `CMD(0) | SEQ(1) | UVLO(2) | Pre-alloc(3) | RSVD(4-6) | OVLO(7) | Chip Number(8) | Not Sup Chip(9) | RSVD(10) | CHKSM(11)`
- UVLO: UVLO threshold = **33V + UVLO × 64.45mV**
- Pre-alloc: System Pre-allocated enable status — 0x00 = Disable; 0x01 = Enable
- OVLO: OVLO threshold = **57V + OVLO × 64.45mV**
- Chip Number: Number of PSE chips detected
- Not Sup Chip: Number of PSE chips not supported by SDK

### 5.12 Global PM Configuration Get CMD (0x4B) [App]

Get system power management mode and power bank configuration of PoE subsystem.

Request (Table 5-23): `CMD(0) | Bank ID(1) | RSVD(2-10) | CHKSM(11)` — note: **byte1 carries Bank ID instead of SEQ**
- Bank ID: 0x00-0x07 = valid bank ID; 0x08-0xFF = Request failed

Response (Table 5-24): `CMD(0) | Bank ID(1) | PM Mode(2) | Bank ID Total Power(3-4) | Bank ID Reserved Power(5-6) | Bank ID+1 Total Power(7-8) | Bank ID+1 Reserved Power(9-10) | CHKSM(11)`
- Bank ID: 0x00-0x07 = valid bank ID; 0x08-0xFF = Request failed
- PM Mode: 0x00 = None; 0x01 = Static mode with priority; 0x02 = Dynamic mode with priority; 0x03 = Static mode without priority; 0x04 = Dynamic mode without priority
- Bank ID Total Power: Total power of Bank ID (**0.1W/LSB**)
- Bank ID Reserved Power: Reserved power of Bank ID (**0.1W/LSB**)

### 5.13 Global Device Address Get CMD (0x4C) [App]

Get I2C address of PSE chips identified by PoE controller.

Request (Table 5-25): `CMD(0) | SEQ(1) | Idx(2) | RSVD(3-10) | CHKSM(11)`
- Idx: 0x00-0x0B = Chip_index; 0x0C-0xFF = invalid index

Response (Table 5-26): `CMD(0) | SEQ(1) | Idx(2) | Addr Chip[idx](3) | Addr Chip[idx+1](4) | Addr Chip[idx+2](5) | Addr Chip[idx+3](6) | Addr Chip[idx+4](7) | Addr Chip[idx+5](8) | Addr Chip[idx+6](9) | Addr Chip[idx+7](10) | CHKSM(11)`
- Idx: 0x00-0x0B = chip index
- Offset+n: I2C low address of chip[idx+n]. **If there is no device present in the specified idx+n, the device address will be filled with 0xFF.**

### 5.14 Port Function Mode Get CMD (0x4D) [App]

Get PSE port function mode. The PSE function mode could be: **semi-auto, auto and manual mode**.
- **Semi-auto**: the specific port will do detection, classification automatically, but finally host CPU will inform the PoE controller whether the PD can be powered on or return to IDLE according to the calculation.
- **Auto**: the specific port will do detection, classification automatically, and the PoE controller can decide whether to power on this port or return to IDLE according to predetermined rules.
- **Manual**: the port needs to be triggered by host CPU step by step to do detection, classification and power up.

Request (Table 5-27): `CMD(0) | SEQ(1) | Port(2) | RSVD(3) | Port(4) | RSVD(5) | Port(6) | RSVD(7) | Port(8) | RSVD(9) | RSVD(10) | CHKSM(11)` — up to 4 ports per request (bytes 2/4/6/8)
- Port: 0x00-0x2F = valid logical port index (0-47); 0x30-0xFF = invalid

Response (Table 5-28): `CMD(0) | SEQ(1) | (Port,VAL)×4 (2-9) | RSVD(10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid
- VAL: 0x00 = Auto mode; 0x01 = Semi-auto mode; 0x02 = Manual mode

### 5.15 Channel Status Get CMD (0x4E) [App]

Get primary channel and secondary channel status of the specified port. This command can get basic channel information of the specified port: power state, fault status, detection result, classification result and PD type.

Request (Table 5-29): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-30): `CMD(0) | SEQ(1) | Port(2) | STS1(3) | STS2(4) | STS3(5) | STS4(6) | STS5(7) | STS6(8) | STS7(9) | STS8(10) | CHKSM(11)`
- STS1/STS5 — Primary/secondary channel detection status: 0x0 = Unknown; 0x1 = Short Circuit; 0x2 = High Cap; 0x3 = Rlow; 0x4 = Valid PD; 0x5 = Rhigh; 0x6 = Open Circuit; 0x7 = FET Failure; 0x8-0xF = Reserved
- STS2/STS6 — Primary/secondary channel classification status: 0x0-0x8 = PD class numbers; 0x9-0xB = Reserved; 0xC = PD treated as Class 0; 0xD = RSVD; 0xE = Class Mismatch; 0xF = Class over Current
- STS3/STS7 — Primary/secondary channel fault status: 0x0 = OVLO; 0x1 = MPS Absent; 0x2 = Short; 0x3 = Overload; 0x4 = Power Denied; 0x5 = Thermal Shutdown; 0x6 = Inrush fail; 0x7 = UVLO; 0xE = GOTP
- STS4/STS8 — Primary/secondary channel power status: 0x0 = Disabled; 0x1 = Searching; 0x2 = Delivering Power; 0x3 = RSVD; 0x4 = Fault; 0x5 = RSVD; 0x6 = Requesting Power

### 5.16 Port Channel Voltage Current Get CMD (0x4F) [App]

Get two channel's voltage and current which belongs to a specific port.

Request (Table 5-31): `CMD(0) | SEQ(1) | Port(2) | RSVD(3-10) | CHKSM(11)`
- Port: 0x00-0x2F valid; 0x30-0xFF invalid

Response (Table 5-32): `CMD(0) | SEQ(1) | Port(2) | Pri_Volt(3-4) | Pri_Curr(5-6) | Sec_Volt(7-8) | Sec_Curr(9-10) | CHKSM(11)` — _(doc caption mistakenly says "Port Mib Counter Get CMD", a doc typo)_
- Pri_Volt: Pri-channel Voltage (**64.45mV/LSB**)
- Pri_Curr: Pri-channel Current (**1mA/LSB**)
- Sec_Volt: Sec-channel Voltage (**64.45mV/LSB**)
- Sec_Curr: Sec-channel Current (**1mA/LSB**)

### 5.17 System Chip Type Information Get CMD (0x50) [App]

Get all the PSE chip type informations of PoE subsystem.

Request (Table 5-33): `CMD(0) | SEQ(1) | RSVD(2-10) | CHKSM(11)`

Response (Table 5-34): `CMD(0) | SEQ(1) | STS1(2) | STS2(3) | STS3(4) | STS4(5) | STS5(6) | STS6(7) | RSVD(8-10) | CHKSM(11)`
- STS1:
  - BIT7-4 = PSE type of chip0: 0x1 = 8 channel bt chip; 0x2 = 8 channel at chip; 0xE = Information get failure; 0xF = Reserved
  - BIT3-0 = PSE type of chip1: same encoding as above
- STS2-STS6: same format as STS1 — BIT7-4 of STS2 = PSE type of chip2, BIT3-0 of STS2 = chip3, and so on. This command can display PSE chip type informations for up to **12 chips**.

## 6. Miscellaneous Commands — Details

### 6.1 Jump To Loader CMD (CMD-SUB ID=0xC0-00) [App]

Jump to Loader area from App area. For firmware upgrades, this command will make pointers jump to Loader area from App area. Firmware information on flash memory will be **erased simultaneously** — the information includes App image length and checksum.

**After this command is responded, it should wait for a certain period of time before sending the next command.** For example: when the MCU flash is larger than 128KB (like GD303), wait about **8 seconds**; when the MCU flash is 64KB (like GD230), wait about **2 seconds**.

Request (Table 6-1): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x00)

Response (Table 6-2): `CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 6.2 Configuration Information Save CMD (0xC0-01) [App]

Save configuration information to flash. The information includes system and per-port parameters. The saved configuration may affect next startup items.

Request (Table 6-3): `CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- SUB: Sub command (0x01)
- Year / Month / Day: date of configuration (**only support for RTL8238C/8239C**)
- High Version / Low Version: version of configuration (**only support for RTL8238C/8239C**)

Response (Table 6-4): `CMD(0) | SUB(1) | Save STS(2) | Version STS(3) | RSVD(4-10) | CHKSM(11)`
- Save STS: 0x00 = Request success; 0x01 = Request failed
- Version STS (**only support for RTL8238C/8239C**): 0x00 = Request success; 0x01 = Request failed

### 6.3 Configuration Information Clear CMD (0xC0-02) [App]

Clear configuration information from flash.

Request (Table 6-5): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x02)

Response (Table 6-6): `CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 6.4 Configuration Version Save CMD (0xC0-03) [App]

Save configuration version information to ram and flash. The information includes date and version of configuration. **Only support for RTL8238B/8239.**

Request (Table 6-7): `CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- SUB: Sub command (0x03)
- Year / Month / Day: date of configuration
- High Version / Low Version: version of configuration

Response (Table 6-8): `CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 6.5 Configuration Version Get CMD (0xC0-04) [App]

Get configuration version information. The information includes date and version of configuration.

Request (Table 6-9): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x04)

Response (Table 6-10): `CMD(0) | SUB(1) | Year(2) | Month(3) | Day(4) | High Version(5) | Low Version(6) | RSVD(7-10) | CHKSM(11)`
- Year / Month / Day: date of configuration
- High Version / Low Version: version of configuration

### 6.6 Configuration Information Reset CMD (0xC0-05) [App]

Reset configuration information to default settings.

Request (Table 6-11): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x05)

Response (Table 6-12): `CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 6.7 Configuration Information Initial CMD (0xC0-06) [App]

Check effectiveness of configuration information, determine whether configuration information reset is required.

Request (Table 6-13): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x06)

Response (Table 6-14): `CMD(0) | SUB(1) | STS(2) | RSVD(3-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 6.8 App Download CMD (CMD-SUB ID=0xC0-80 ~ 83) [Loader only]

The Application Image can be directly downloaded to the PoE Controller MCU from the host CPU. This command is supported **only when the PoE Controller is running in bootloader**.

Request (Table 6-15, up to 36 bytes): `CMD(0) | SUB(1) | Image Offset(2-3) | Data0(4) ... Data31(35)`
- SUB: 0x80 = 0~64K image block; 0x81 = 64K~128K image block; 0x82 = 128K~192K image block; 0x83 = 192K~256K image block
- Image Offset: 0x0000-0xFFFF (Up to 64KB), shows the offset of each 64KB frame in the application image
- Data: Image data (**4~32 bytes/frame, 4-Byte aligned**)

Notes:
- **The last App Download CMD should only send the remaining bytes of the firmware; it is forbidden to fill the fixed length with value 0xFF or 0x0. Otherwise, it will cause the upgrade fail.**
- If Data length is less than 32 (such as 4/8/12/16/20/24/28), enough delay time between App Download CMDs should be given to make sure the receive timeout has ended.
- **In Loader code, only Loader commands (App Download CMD, Jump To Loader CMD, Firmware download CMD) will receive response. All the App commands can not get any response. All the Loader commands and response length is not fixed and do not have checksum byte.**
- **In App code, all commands, whether it is Loader command or App command will get response as long as the command is at least 12 bytes.**

Response (Table 6-16, 4 bytes): `CMD(0) | SUB(1) | Image Offset(2-3)`
- SUB: 0x80 = 0~64K; 0x81 = 64K~128K; 0x82 = 128K~192K; 0x83 = 192K~256K image block
- Image Offset: 0x0000-0xFFFF (Up to 64KB), offset of each 64KB frame in the application image

### 6.9 Jump To App CMD (CMD-SUB ID=0xC0-40) [Loader only]

Jump to App area from Loader area. This command is used to execute CheckSum routine after app image downloading. If the result of check is passed, PoE controller will automatically jump to app. If the result is failed, App area and external SPI Flash (if necessary) will be erased for the next upgrade.
**This command will be responded within one second. Before the response, no command should be sent.**

Request (Table 6-17): `CMD(0) | SUB(1) | RSVD(2-10) | CHKSM(11)`
- SUB: Sub command (0x40)

Notes:
- **If any of APP or Firmware image (if needed) is invalid, both APP and Firmware area will be erased then wait for the next upgrade.**
- Loader/App code response rules: same as CMD 0xC0-80~83 (Loader code responds only to Loader commands without checksum; App code responds to all commands ≥12 bytes).

Response (Table 6-18): `CMD(0) | SUB(1) | STS1(2) | STS2(3) | RSVD(4-10) | CHKSM(11)`
- STS1: 0x00 = App image valid check passed; 0x01 = App image is not valid
- STS2: 0x00 = Firmware image valid check passed; 0x01 = Firmware image is not valid

### 6.10 Firmware Download CMD (CMD ID=0xCA) [Loader only]

In BT projects, external SPI Flash is needed for some MCUs with small flash. The Firmware Image can be directly downloaded to the external SPI Flash. This command is supported **only when the PoE Controller is running in bootloader**.

Request (Table 6-19, up to 36 bytes): `CMD(0) | Seq(1) | Image Offset(2-3) | Data0(4) ... Data31(35)`
- Image Offset: 0x0000-0xFFFF (Up to 64KB), shows the offset of this frame in the application image
- Data: Image data (**4/8/16/32 bytes supported**)

Notes:
- **For early Loader firmware, only 32byte Data length is supported**, and this length is suggested for high efficiency. If 4/8/16 length is needed, please check with Realtek if new Loader firmware should be released.
- If Data length is less than 32 (such as 4/8/16), enough delay time between Firmware download CMDs should be given to make sure the receive timeout has ended.
- Loader/App code response rules: same as CMD 0xC0-80~83.

Response (Table 6-20, 4 bytes): `CMD(0) | Seq(1) | Image Offset(2-3)`
- Image Offset: 0x0000-0xFFFF (Up to 64KB), offset of this frame in the application image

## 7. Debug Commands — Details

### 7.1 Chip Register Set CMD (0xF0) [App]

Set a register value of a PoE chip.

Request (Table 7-1): `CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | Register Value(7-10) | CHKSM(11)`
- ChipAddr: 0x20 – 0x37 = Valid chip I2C address
- Register Address: 32bits register address
- Register Value: 32bits register value

Response (Table 7-2): `CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | STS(7) | RSVD(8-10) | CHKSM(11)`
- STS: 0x00 = Request success; 0x01 = Request failed

### 7.2 Chip Register Get CMD (0xF1) [App]

Get a register value of a PoE chip.

Request (Table 7-3): `CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | RSVD(7-10) | CHKSM(11)`
- ChipAddr: 0x20 – 0x37 = Valid chip I2C address
- Register Address: 32bits register address

Response (Table 7-4): `CMD(0) | SEQ(1) | ChipAddr(2) | Register Address(3-6) | Register Value(7-10) | CHKSM(11)`
- Register Value: 32bits register value

## 8. Revision Notes (Rev.2.2 highlights)

- 2.2: Modified description of CMD-0xC, 0xD, 0x5, 0x43, 0xCA and 0xC0 40.
- 2.1: CMD-0xE Byte 2 (UVLO) / Byte 4 (OVLO) description; CMD-0x44 Byte[7-8] temperature correction; CMD-0x4B response Byte[2]=0x2; CMD-0xC0 02 Byte[1] correction.
- 2.0: CMD-0x4A Byte[9] = number of PSE ICs not supported by SDK; CMD-0x13/14 AT/BT recommended max power; CMD-0x45 4KB RAM MCU limitation.
- 1.9: CMD-0x42/43/4E GOTP & inrush fail status; CMD-0x12/13/14 relationship; notes for CMD-0xC0 80, 0xC0 40, 0xCA.
- 1.8: CMD-0x5 port mapping start/end; CMD-0x42/43 GOTP fault status; CMD-0x19 short cable function; CMD-0x48 Byte[10] short cable display; CMD-0xC0 01 config version (RTL8238C/8239C).
