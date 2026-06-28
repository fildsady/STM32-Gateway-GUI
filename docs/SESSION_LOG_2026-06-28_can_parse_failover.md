# Session Log: CAN Parse Fix + Bus Failover

**วันที่:** 2026-06-28

## สรุป

แก้ GUI parse CAN frame ผิด protocol, เพิ่ม failover CAN↔Modbus อัตโนมัติ, แก้ serial desync และ RegMap แย่ง port

## ปัญหาที่ 1: ParseCanFrame ใช้ mapping ผิด

GUI เดิมใช้ field mapping แบบ Modbus — ไม่ได้แยก CAN 3-frame structure

| CAN Frame | ข้อมูลจริง | GUI เดิม |
|-----------|-----------|---------|
| Status (0x300) | state/track/vol/repeat/sample/elapsed/count | ✗ byte 5,6 ไปเป็น temp/group |
| Name (0x200) | format + 7-char name | ✗ ใช้ multi-frame seq |
| Extra (0x400) | temp/group/mcast/mono/auto/rtc | ✗ ไม่ parse เลย |

### ข้อจำกัด CAN vs Modbus

| Field | CAN | Modbus |
|-------|-----|--------|
| Uptime | ✗ | 0x0020+0x0028 |
| FW version | ✗ | 0x0022/0x0023 |
| Heap free | ✗ | 0x0025 |
| Temp | integer °C | ×10 |
| Track name | max 7 chars | max 32 chars |

แก้: เขียน `ParseCanFrame` ใหม่ตรง CAN_PROTOCOL.md — field ที่ CAN ไม่มีแสดง "(CAN)"

## ปัญหาที่ 2: RxLoop byte desync

`_port.Read(buf, 0, 3)` ไม่รับประกันอ่านครบ → parse เลื่อน → ค่าเด้ง

แก้: loop `while (got < 3) got += _port.Read(buf, got, 3 - got);`

## ปัญหาที่ 3: Register Map แย่ง serial

`ReadMbBlock()` อ่าน serial port ตรงขณะ `RxLoop` อ่านอยู่ → 2 thread แย่ง byte

แก้: `_regMapBusy` flag — PollLoop หยุด + RxLoop ไม่ parse MB response ตอน RegMap ทำงาน

## ปัญหาที่ 4: Settings ไม่จำค่า

Event handler ถูก trigger ตอน `LoadSettings()` set ค่า → `SaveSettings()` ทับด้วย default

แก้: `_loading` guard — ข้าม save ระหว่าง constructor

## ฟีเจอร์ใหม่: Bus Failover

dropdown "Bus:" เดียวแทน radio button เดิม:

| Mode | พฤติกรรม |
|------|---------|
| CAN only | CAN อย่างเดียว |
| Modbus only | Modbus อย่างเดียว |
| CAN → Modbus | CAN หลัก ถ้าหลุด→สลับ MB ถ้ากลับ→สลับกลับ |
| Modbus → CAN | MB หลัก ถ้าหลุด→สลับ CAN ถ้ากลับ→สลับกลับ |

- Timeout: ไม่ได้ RX ใน 3× poll interval → switch
- Probe: ทุก 5 poll ยิง GET ไป primary → ได้ RX → switch กลับ
- Status: `● CAN` / `● MB`

## ไฟล์ที่แก้

- `MainWindow.xaml` — Bus mode dropdown, ลบ radio button
- `MainWindow.xaml.cs` — ParseCanFrame ตรง protocol, RxLoop read ครบ, failover, RegMap flag, settings save/load ครบ + loading guard

## บทเรียน

- **อ่าน protocol doc ก่อน parse** — CAN 3-frame ≠ Modbus register map
- **Serial read ต้อง loop จนครบ** — `Read()` อาจให้ไม่ครบ
- **Serial port 1 thread เท่านั้น** — ใช้ flag กั้น ห้าม 2 thread อ่านพร้อมกัน
- **Event handler trigger ตอน load** — ต้องมี guard flag
- **GUI ต้อง save state ทุก field** — เพิ่ม control ใหม่ต้องเพิ่ม save ด้วย
