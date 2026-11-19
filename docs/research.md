# 调研笔记（2026-11-17）

## 协议细节摘录

### ATK / VXE / VGN（含 ATK 键盘）
- 16 字节 Output/Input 报文，Report ID `0x08`
- 校验和：`(0x55 - (0x08 + sum(data[0..14]))) & 0xFF`
- 电量命令 `0x04`，回复包 `data[5]`=电量百分比，`data[6]`=充电标志，`data[7..8]`=毫伏电压
- ATK 键盘（A98 Pro `373B:1152`）走 VIA 之上的 customId 扩展：`00 02 96 24` 查电池
- 参考：vigil-watt `docs/hardware/atk.md`、Wick `src-tauri/src/providers/hid/atk_vxe.rs`、BatteryDuo `atk_a9_battery.py`

### CompX / PixArt 家族（兰族 Lamzu、万灵 WLmouse、Pulsar、Attack Shark 等）
- 65 字节 Feature Report，Report ID `0x00`
- 电量查询：`00 00 00 02 02 00 83 ...`
- 回复：`A1 00 02 02 00 83 [充电标志] [电量百分比]`
- 覆盖型号：Lamzu Inca 8K/Paro/Atlantis/Thorn/Maya；WLmouse Beast X 全系/Sword X 等
- 来源：FerrisPulse 调研文档

### ROG（华硕）
- 鼠标/键盘私有 HID 协议；键盘电池与鼠标共享协议 `12 01`
- ROG 耳机（Delta II 等）：统一电池查询 `CC 12 07`
- 参考：youmikk/ROG-Battery、measureer/ROGDeltaTray、vigil-watt `docs/hardware/asus-rog.md`

### 雷蛇 / 罗技 / 迈从
- 直接参考 Vex9th/mouse-control 的协议文档：`docs/MOUSE_PROTOCOL.md`、`docs/LOGITECH_PROTOCOL.md`、`docs/MCHOSE_PROTOCOL.md`
- 罗技走 HID++ 2.x 动态发现能力；接收器（LIGHTSPEED/Bolt/Unifying）按槽位识别

### 怒喵 Angry Miao
- 参考 SinkingWhite/AngryMiaoBatteryMgr（电量 + DPI）

## 风险与注意
- 官方驱动（雷云/G HUB/Armoury Crate/ATK HUB 等）与本项目可能争用 HID 独占通道，读/写前建议关闭或做成可切换
- 写帧协议（主动查询）需要按品牌做开关，默认谨慎只读
- 接收器一个 dongle 带多设备时按槽位区分；同一 VID/PID 可能对应多型号（如 AULA 与凌豹 3554:FA09 靠会话型号码区分）
- 蓝牙连接的设备走 BLE GATT 电池服务或 HFP 节点，与 2.4G/有线通道不同
