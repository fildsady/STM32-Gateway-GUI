using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace STM32GatewayGui;

public partial class MainWindow : Window
{
    const byte CMD_CAN_TX = 0x01, CMD_CAN_RX = 0x02;
    const byte CMD_MB_TX  = 0x03, CMD_MB_RX  = 0x04;

    const byte CAN_CMD_PLAY = 1, CAN_CMD_STOP = 2, CAN_CMD_NEXT = 3;
    const byte CAN_CMD_PREV = 4, CAN_CMD_PAUSE = 5, CAN_CMD_VOL = 6;
    const byte CAN_CMD_REPEAT = 7, CAN_CMD_MONO = 8, CAN_CMD_AUTO = 9;
    const byte CAN_CMD_GET = 0x10;

    const uint CAN_ID_CMD_BASE = 0x100, CAN_ID_NAME_BASE = 0x200, CAN_ID_STATUS_BASE = 0x300, CAN_ID_EXTRA_BASE = 0x400;

    static readonly string[] StateNames = ["Stop", "Play", "Error", "Pause"];

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private int _txCount, _rxCount, _lastState, _pollCycle;
    private int _trafficTx, _trafficRx;
    private volatile byte[]? _faultResponse;
    private int _pollMs = 500, _gapMs = 20;
    static readonly int[] PollRates = [100, 250, 500, 1000, 2000, 5000];
    static readonly int[] GapRates = [10, 20, 50, 100, 200];
    private bool _suppressVol, _suppressRepeat, _monoState, _autoplayState = true;
    private ushort _uptimeLow;
    private bool _uptimeHiPending;
    private DateTime _lastVolSend = DateTime.MinValue;
    // Bus mode: 0=CAN only, 1=MB only, 2=CAN→MB, 3=MB→CAN
    private int _busMode = 2;
    private bool _activeBusCan = true;
    private DateTime _lastActiveRx = DateTime.Now;
    private bool _activeOk = true;
    private int _probeCount = 0;
    private readonly string[] _nameFrames = new string[20];

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
    private static readonly string WindowPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window.txt");

    private bool _loading = true;
    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowState();
        SizeChanged += (_, _) => SaveWindowState();
        LocationChanged += (_, _) => SaveWindowState();
        StateChanged += (_, _) => SaveWindowState();
        RefreshPorts();
        LoadSettings();
        _loading = false;
    }

    // ── Window state ────────────────────────────────────────────
    private void SaveWindowState()
    {
        try
        {
            if (!IsLoaded) return;
            double l = Left, t = Top, w = Width, h = Height;
            if (double.IsNaN(l) || w < 100) return;
            if (WindowState == WindowState.Maximized)
            { var b = RestoreBounds; l = b.Left; t = b.Top; w = b.Width; h = b.Height; }
            File.WriteAllText(WindowPath, $"{l:F0},{t:F0},{w:F0},{h:F0},{(WindowState == WindowState.Maximized ? 1 : 0)}");
        }
        catch { }
    }

    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(WindowPath)) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
            var p = File.ReadAllText(WindowPath).Trim().Split(',');
            if (p.Length < 5) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
            Left = double.Parse(p[0]); Top = double.Parse(p[1]);
            Width = double.Parse(p[2]); Height = double.Parse(p[3]);
            if (p[4] == "1") WindowState = WindowState.Maximized;
        }
        catch { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
    }

    // ── Settings ────────────────────────────────────────────────
    private void RefreshPorts()
    {
        CmbPort.Items.Clear();
        foreach (var p in SerialPort.GetPortNames()) CmbPort.Items.Add(p);
        if (CmbPort.Items.Count > 0) CmbPort.SelectedIndex = 0;
    }

    private void SaveSettings()
    {
        if (_loading) return;
        try
        {
            string port = CmbPort.SelectedItem?.ToString() ?? "";
            File.WriteAllText(SettingsPath, string.Join(",",
                port, TxtCanTarget.Text, TxtMbSlave.Text,
                CmbBusMode.SelectedIndex, CmbMbBaud.SelectedIndex, CmbCanBaud.SelectedIndex,
                CmbPollRate.SelectedIndex, CmbGap.SelectedIndex));
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var p = File.ReadAllText(SettingsPath).Trim().Split(',');
            if (p.Length >= 1) for (int i = 0; i < CmbPort.Items.Count; i++)
                if (CmbPort.Items[i].ToString() == p[0]) { CmbPort.SelectedIndex = i; break; }
            if (p.Length >= 2) TxtCanTarget.Text = p[1];
            if (p.Length >= 3) TxtMbSlave.Text = p[2];
            if (p.Length >= 4 && int.TryParse(p[3], out int bm) && bm >= 0 && bm <= 3) { CmbBusMode.SelectedIndex = bm; _busMode = bm; _activeBusCan = bm == 0 || bm == 2; }
            if (p.Length >= 5 && int.TryParse(p[4], out int mbi) && mbi >= 0 && mbi < 7) CmbMbBaud.SelectedIndex = mbi;
            if (p.Length >= 6 && int.TryParse(p[5], out int cbi) && cbi >= 0 && cbi < 6) CmbCanBaud.SelectedIndex = cbi;
            if (p.Length >= 7 && int.TryParse(p[6], out int pri) && pri >= 0 && pri < PollRates.Length) { CmbPollRate.SelectedIndex = pri; _pollMs = PollRates[pri]; }
            if (p.Length >= 8 && int.TryParse(p[7], out int gri) && gri >= 0 && gri < GapRates.Length) { CmbGap.SelectedIndex = gri; _gapMs = GapRates[gri]; }
        }
        catch { }
    }

    private void Log(string msg)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        Dispatcher.BeginInvoke(() => { TxtLog.AppendText($"[{ts}] {msg}\n"); TxtLog.ScrollToEnd(); });
    }

    // ── Connection ──────────────────────────────────────────────
    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_port != null) { Disconnect(); return; }
        if (CmbPort.SelectedItem == null) return;
        try
        {
            _port = new SerialPort(CmbPort.SelectedItem.ToString()!, 921600, Parity.None, 8, StopBits.One)
            { ReadTimeout = 500, WriteTimeout = 500 };
            _port.Open();
            _cts = new CancellationTokenSource();
            Task.Run(() => RxLoop(_cts.Token));
            Task.Run(() => PollLoop(_cts.Token));
            BtnConnect.Content = "Disconnect";
            TxtStatus.Text = "Connected";
            TxtStatus.Foreground = FindResource("CatGreen") as System.Windows.Media.Brush;
            SaveSettings(); Log("Connected");
        }
        catch (Exception ex) { Log($"Failed: {ex.Message}"); }
    }

    private void Disconnect()
    {
        _cts?.Cancel(); _port?.Close(); _port?.Dispose(); _port = null;
        BtnConnect.Content = "Connect";
        TxtStatus.Text = "Disconnected";
        TxtStatus.Foreground = FindResource("CatRed") as System.Windows.Media.Brush;
        Log("Disconnected");
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void CmbPollRate_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int idx = CmbPollRate.SelectedIndex;
        if (idx >= 0 && idx < PollRates.Length) _pollMs = PollRates[idx];
        SaveSettings();
    }
    private void CmbBusMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _busMode = CmbBusMode.SelectedIndex;
        _activeBusCan = _busMode == 0 || _busMode == 2;
        _lastActiveRx = DateTime.Now;
        _activeOk = true;
        _probeCount = 0;
        SaveSettings();
    }
    private bool CanEnabled => _busMode == 0 || _busMode == 2 || _busMode == 3;
    private bool MbEnabled => _busMode == 1 || _busMode == 2 || _busMode == 3;
    private bool CanPrimary => _busMode == 0 || _busMode == 2;

    private void CheckFailover()
    {
        if (_busMode <= 1) return; // only mode, no failover

        if ((DateTime.Now - _lastActiveRx).TotalMilliseconds > _pollMs * 3)
            _activeOk = false;

        if (!_activeOk)
        {
            _activeBusCan = !_activeBusCan;
            _lastActiveRx = DateTime.Now;
            _activeOk = true;
            _probeCount = 0;
        }

        // Probe primary every 5 polls while on secondary
        bool onPrimary = _activeBusCan == CanPrimary;
        if (!onPrimary) _probeCount++;
    }
    private bool ShouldUseCan() { return _activeBusCan; }
    private void UpdateBusStatus()
    {
        string bus = _activeBusCan ? "CAN" : "MB";
        string st = _activeOk ? "●" : "…";
        Dispatcher.BeginInvoke(() => TxtBusStatus.Text = $"{st} {bus}");
    }

    private void CmbGap_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int idx = CmbGap.SelectedIndex;
        if (idx >= 0 && idx < GapRates.Length) _gapMs = GapRates[idx];
        SaveSettings();
    }

    static readonly string[] MbBaudNames = ["9600","19200","38400","57600","115200","230400","460800"];
    static readonly string[] CanBaudNames = ["20k","50k","125k","250k","500k","1M"];

    private void BtnSetMbBaud_Click(object sender, RoutedEventArgs e)
    {
        int idx = CmbMbBaud.SelectedIndex;
        if (idx < 0 || _port == null || !_port.IsOpen) return;
        try { _port.Write(new byte[] { 0xFE, 0xFE, 0x01, (byte)idx }, 0, 4); Log($"MB baud → {MbBaudNames[idx]}"); } catch { }
        SaveSettings();
    }

    private void BtnSetCanBaud_Click(object sender, RoutedEventArgs e)
    {
        int idx = CmbCanBaud.SelectedIndex;
        if (idx < 0 || _port == null || !_port.IsOpen) return;
        try { _port.Write(new byte[] { 0xFE, 0xFE, 0x02, (byte)idx }, 0, 4); Log($"CAN baud → {CanBaudNames[idx]}"); } catch { }
        SaveSettings();
    }

    private void BtnFaultLog_Click(object sender, RoutedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            _faultResponse = null;
            _port.Write(new byte[] { 0xFE, 0xFE, 0x10 }, 0, 3);
            Log("Requesting fault log...");
            Task.Run(() =>
            {
                var start = DateTime.Now;
                while (_faultResponse == null && (DateTime.Now - start).TotalMilliseconds < 1000)
                    Thread.Sleep(10);

                byte[]? resp = _faultResponse;
                _faultResponse = null;

                if (resp == null || resp.Length < 4) { Dispatcher.BeginInvoke(() => Log("No fault data")); return; }

                int count = resp[3];
                if (count == 0) { Dispatcher.BeginInvoke(() => Log("No faults recorded")); return; }

                int entrySize = 17;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Fault Log ===");
                string[] types = ["?", "HardFault", "StackOverflow", "WDT", "Assert"];
                for (int i = 0; i < count; i++)
                {
                    int off = 4 + i * entrySize;
                    if (off + entrySize > resp.Length) break;
                    int type = resp[off];
                    uint pc = BitConverter.ToUInt32(resp, off + 1);
                    uint lr = BitConverter.ToUInt32(resp, off + 5);
                    uint uptime = BitConverter.ToUInt32(resp, off + 9);
                    uint cfsr = BitConverter.ToUInt32(resp, off + 13);
                    string tname = type < types.Length ? types[type] : $"0x{type:X2}";
                    sb.AppendLine($"[{i}] {tname} PC=0x{pc:X8} LR=0x{lr:X8} up={uptime}s CFSR=0x{cfsr:X8}");
                }

                string logText = sb.ToString();
                Dispatcher.BeginInvoke(() =>
                {
                    Log(logText);
                    var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text|*.txt", FileName = "fault_log.txt" };
                    if (dlg.ShowDialog() == true)
                    {
                        System.IO.File.WriteAllText(dlg.FileName, logText);
                        Log($"Saved: {dlg.FileName}");
                    }
                });
            });
        }
        catch { }
    }

    // ── RX Loop ─────────────────────────────────────────────────
    private void RxLoop(CancellationToken ct)
    {
        byte[] buf = new byte[64];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_port == null || !_port.IsOpen) break;
                int b = _port.ReadByte();
                if (b == 0xFE)
                {
                    int b2 = _port.ReadByte();
                    if (b2 == 0xFE)
                    {
                        int cmd = _port.ReadByte();
                        if (cmd == 0x10)
                        {
                            int count = _port.ReadByte();
                            int entrySize = 17;
                            byte[] data = new byte[4 + count * entrySize];
                            data[0] = 0xFE; data[1] = 0xFE; data[2] = 0x10; data[3] = (byte)count;
                            int read = 0, total = count * entrySize;
                            var t = DateTime.Now;
                            while (read < total && (DateTime.Now - t).TotalMilliseconds < 500)
                            {
                                if (_port.BytesToRead > 0) read += _port.Read(data, 4 + read, total - read);
                                else Thread.Sleep(1);
                            }
                            _faultResponse = data;
                        }
                    }
                    continue;
                }

                if (b == CMD_CAN_RX)
                {
                    int got = 0;
                    while (got < 3) got += _port.Read(buf, got, 3 - got);
                    uint id = (uint)((buf[0] << 8) | buf[1]);
                    int dlc = buf[2]; if (dlc > 8) dlc = 8;
                    got = 0;
                    while (got < dlc) got += _port.Read(buf, 3 + got, dlc - got);
                    byte[] data = new byte[dlc];
                    Array.Copy(buf, 3, data, 0, dlc);
                    if (_activeBusCan) { _lastActiveRx = DateTime.Now; _activeOk = true; }
                    else if (CanPrimary && _busMode >= 2) { _activeBusCan = true; _lastActiveRx = DateTime.Now; _activeOk = true; }
                    Dispatcher.BeginInvoke(() => { _rxCount++; ParseCanFrame(id, data); TrafficRx($"CAN 0x{id:X3} [{dlc}] {BitConverter.ToString(data).Replace("-"," ")}"); });
                }
                else if (b == CMD_MB_RX)
                {
                    int len = _port.ReadByte();
                    if (len <= 0 || len > 250) continue;
                    byte[] resp = new byte[len];
                    int read = 0;
                    while (read < len) read += _port.Read(resp, read, len - read);
                    if (_regMapBusy) continue;
                    if (!_activeBusCan) { _lastActiveRx = DateTime.Now; _activeOk = true; }
                    else if (!CanPrimary && _busMode >= 2) { _activeBusCan = false; _lastActiveRx = DateTime.Now; _activeOk = true; }
                    Dispatcher.BeginInvoke(() => { _rxCount++; ParseMbResponse(resp, len); TrafficRx($"MB [{len}] {BitConverter.ToString(resp).Replace("-"," ")}"); });
                }
            }
            catch (TimeoutException) { }
            catch { if (!ct.IsCancellationRequested) break; }
        }
    }

    // ── Poll Loop ───────────────────────────────────────────────
    private void PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_regMapBusy) { Thread.Sleep(_pollMs); continue; }
                CheckFailover();
                UpdateBusStatus();
                bool useCan = ShouldUseCan();

                if (useCan)
                {
                    SendCanFrame(CAN_ID_CMD_BASE + GetCanTarget(), [CAN_CMD_GET, 0]);
                }
                else if (MbEnabled)
                {
                    byte slave = GetMbSlave();
                    int gap = _gapMs;
                    SendMbRead(slave, 0x0000, 9); Thread.Sleep(gap);
                    SendMbRead(slave, 0x0020, 8); Thread.Sleep(gap);
                    Dispatcher.Invoke(() => _uptimeHiPending = true);
                    SendMbRead(slave, 0x0028, 1); Thread.Sleep(gap);
                    SendMbRead(slave, 0x0012, 3); Thread.Sleep(gap);
                    if (_pollCycle % 3 == 0)
                        SendMbRead(slave, 0x0100, 16);
                    else if (_pollCycle % 3 == 1)
                        SendMbRead(slave, 0x0015, 6);
                    _pollCycle++;
                }

                // Probe primary every 5 polls while on secondary
                if (_busMode >= 2 && _probeCount >= 5)
                {
                    _probeCount = 0;
                    if (CanPrimary)
                        SendCanFrame(CAN_ID_CMD_BASE + GetCanTarget(), [CAN_CMD_GET, 0]);
                    else
                        SendMbRead(GetMbSlave(), 0x0000, 1);
                }
            }
            catch { }
            Thread.Sleep(_pollMs);
        }
    }

    // ── Parse CAN ───────────────────────────────────────────────
    private void ParseCanFrame(uint id, byte[] data)
    {
        // Frame 1: Status (0x300+N) — state/track/vol/repeat/samplekHz/elapsed/trackcount
        if (id >= CAN_ID_STATUS_BASE && id <= CAN_ID_STATUS_BASE + 0x7F && data.Length >= 8)
        {
            int state = data[0]; _lastState = state;
            TxtState.Text = $"State: {(state < StateNames.Length ? StateNames[state] : "?")}";
            BtnPlayPause.Content = state == 1 ? "⏸ Pause" : "▶ Play";
            TxtTrack.Text = $"Track: {data[1] + 1} / {data[7]}";
            _suppressVol = true;
            TxtVolume.Text = $"Volume: {data[2]}%";
            SliderVol.Value = data[2]; TxtVol.Text = $"{data[2]}%";
            _suppressVol = false;
            string[] repNames = ["All","One","Off","Single","Random"];
            TxtRepeat.Text = $"Repeat: {(data[3] < repNames.Length ? repNames[data[3]] : "?")}";
            TxtFormat.Text = $"{data[4]}kHz  {data[5]}:{data[6]:D2}";
            TxtSD.Text = $"SD: {data[7]} files";
            // CAN ไม่มี uptime/FW/heap
            TxtUptime.Text = "Up: (CAN)";
        }
        // Frame 3: Extra (0x400+N) — temp/group/mcast/mono/autoplay/rtc
        if (id >= CAN_ID_EXTRA_BASE && id <= CAN_ID_EXTRA_BASE + 0x7F && data.Length >= 8)
        {
            TxtTemp.Text = $"Temp: {data[0]}°C";
            _monoState = data[3] != 0;
            _autoplayState = data[4] != 0;
            BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
            BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
            TxtRtcDisplay.Text = $"RTC: {data[5]:D2}:{data[6]:D2}:{data[7]:D2}";
        }
        // Frame 2: Name (0x200+N) — format + 7-char name
        if (id >= CAN_ID_NAME_BASE && id <= CAN_ID_NAME_BASE + 0x7F && data.Length >= 2)
        {
            int fmt = data[0];
            string[] fmtNames = ["?","MP3","WAV","FLAC"];
            string fmtStr = fmt < fmtNames.Length ? fmtNames[fmt] : "?";
            var sb = new StringBuilder();
            for (int i = 1; i < data.Length; i++) { if (data[i] == 0) break; sb.Append((char)data[i]); }
            TxtTrackName.Text = $"♪ [{fmtStr}] {sb}";
        }
    }

    // ── Parse Modbus ────────────────────────────────────────────
    private void ParseMbResponse(byte[] resp, int len)
    {
        if (len < 5 || resp[1] != 0x03) return;
        int bytes = resp[2], count = bytes / 2;
        ushort[] regs = new ushort[count];
        for (int i = 0; i < count && (3 + i * 2 + 1) < len; i++)
            regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[3 + i * 2 + 1]);

        // Update Register Map table from any response
        // Detect block by count + first value range
        if (count == 9 && regs[0] <= 3) UpdateRegMap(0x0000, regs);
        else if (count == 8) UpdateRegMap(0x0020, regs);
        else if (count == 3) UpdateRegMap(0x0012, regs);
        else if (count == 6 && regs[0] >= 2020) UpdateRegMap(0x0015, regs);
        else if (count == 1 && _uptimeHiPending) UpdateRegMap(0x0028, regs);
        else if (count == 16) { /* track name — not in reg map */ }

        if (count == 9 && regs[0] <= 3)
        {
            int state = regs[0]; _lastState = state;
            TxtState.Text = $"State: {(state < StateNames.Length ? StateNames[state] : "?")}";
            BtnPlayPause.Content = state == 1 ? "⏸ Pause" : "▶ Play";
            TxtTrack.Text = $"Track: {regs[1] + 1}/{regs[2]}";
            _suppressVol = true;
            TxtVolume.Text = $"Volume: {regs[3]}%";
            SliderVol.Value = regs[3]; TxtVol.Text = $"{regs[3]}%";
            _suppressVol = false;
            int rep = regs[4];
            TxtRepeat.Text = $"Repeat: {rep}";
            _suppressRepeat = true;
            if (rep < CmbRepeat.Items.Count) CmbRepeat.SelectedIndex = rep;
            _suppressRepeat = false;
            _monoState = regs[5] != 0;
            BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
            _autoplayState = regs[6] != 0;
            BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
            TxtSD.Text = $"SD: {(regs[7] != 0 ? "OK" : "—")}";
        }
        else if (count == 8)
        {
            _uptimeLow = regs[0];
            TxtTemp.Text = $"Temp: {regs[1] / 10.0:F1}°C";
            TxtFwVer.Text = $"FW: {regs[2]}.{regs[3]}";
            TxtHeap.Text = $"Heap: {regs[5] * 16 / 1024}KB";
            uint sr = (uint)regs[6] * 100;
            TxtFormat.Text = sr > 0 ? $"{sr / 1000}kHz" : "—";
        }
        else if (count == 1 && _uptimeHiPending)
        {
            uint upSec = (uint)regs[0] << 16 | _uptimeLow;
            int d = (int)(upSec / 86400), h = (int)(upSec % 86400 / 3600);
            int m = (int)(upSec % 3600 / 60), s = (int)(upSec % 60);
            TxtUptime.Text = $"Up: {d}d {h:D2}:{m:D2}:{s:D2}";
            _uptimeHiPending = false;
        }
        else if (count == 16)
        {
            var sb = new StringBuilder();
            foreach (var r in regs)
            {
                char hi = (char)(r >> 8), lo = (char)(r & 0xFF);
                if (hi != 0) sb.Append(hi); if (lo != 0) sb.Append(lo);
            }
            string name = sb.ToString().TrimEnd('\0');
            if (name.Length > 0) TxtTrackName.Text = $"♪ {name}";
        }
        else if (count == 6 && regs[0] >= 2020)
        {
            string[] dayNames = ["Sun","Mon","Tue","Wed","Thu","Fri","Sat"];
            string dow = "";
            try { dow = dayNames[(int)new DateTime(regs[0], regs[1], regs[2]).DayOfWeek] + " "; } catch { }
            string rtc = $"RTC: {dow}{regs[2]:D2}/{regs[1]:D2}/{regs[0]} {regs[3]:D2}:{regs[4]:D2}:{regs[5]:D2}";
            TxtRtcTime.Text = rtc;
            TxtRtcDisplay.Text = rtc;
        }
    }

    // ── TX helpers ──────────────────────────────────────────────
    private uint GetCanTarget()
    {
        uint t = 0x10;
        Dispatcher.Invoke(() => { if (uint.TryParse(TxtCanTarget.Text.Trim(), NumberStyles.HexNumber, null, out uint v)) t = v; });
        return t;
    }

    private byte GetMbSlave()
    {
        byte s = 1;
        Dispatcher.Invoke(() => { if (byte.TryParse(TxtMbSlave.Text, out byte v)) s = v; });
        return s;
    }

    private void SendCanFrame(uint id, byte[] data)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            byte[] pkt = new byte[4 + data.Length];
            pkt[0] = CMD_CAN_TX; pkt[1] = (byte)(id >> 8); pkt[2] = (byte)(id & 0xFF); pkt[3] = (byte)data.Length;
            Array.Copy(data, 0, pkt, 4, data.Length);
            _port.Write(pkt, 0, pkt.Length); _txCount++;
            TrafficTx($"CAN 0x{id:X3} [{data.Length}] {BitConverter.ToString(data).Replace("-"," ")}");
        }
        catch { }
    }

    private void SendMbRead(byte slave, ushort start, ushort count)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            byte[] req = new byte[8];
            req[0] = slave; req[1] = 0x03;
            req[2] = (byte)(start >> 8); req[3] = (byte)(start & 0xFF);
            req[4] = (byte)(count >> 8); req[5] = (byte)(count & 0xFF);
            var crc = Crc16(req, 6); req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            byte[] pkt = new byte[10]; pkt[0] = CMD_MB_TX; pkt[1] = 8;
            Array.Copy(req, 0, pkt, 2, 8);
            _port.Write(pkt, 0, 10); _txCount++;
            TrafficTx($"MB FC03 S{slave} 0x{start:X4} x{count}");
        }
        catch { }
    }

    private void SendMbWrite(byte slave, ushort addr, ushort value)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            byte[] req = new byte[8];
            req[0] = slave; req[1] = 0x06;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(value >> 8); req[5] = (byte)(value & 0xFF);
            var crc = Crc16(req, 6); req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            byte[] pkt = new byte[10]; pkt[0] = CMD_MB_TX; pkt[1] = 8;
            Array.Copy(req, 0, pkt, 2, 8);
            _port.Write(pkt, 0, 10); _txCount++;
            TrafficTx($"MB FC06 S{slave} 0x{addr:X4}={value}");
        }
        catch { }
    }

    private void SendMbWriteMultiple(byte slave, ushort addr, ushort[] values)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            int count = values.Length;
            byte[] req = new byte[9 + count * 2];
            req[0] = slave; req[1] = 0x10;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(count >> 8); req[5] = (byte)(count & 0xFF);
            req[6] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
            { req[7 + i * 2] = (byte)(values[i] >> 8); req[7 + i * 2 + 1] = (byte)(values[i] & 0xFF); }
            var crc = Crc16(req, 7 + count * 2);
            req[7 + count * 2] = (byte)(crc & 0xFF); req[7 + count * 2 + 1] = (byte)(crc >> 8);
            byte[] pkt = new byte[2 + req.Length]; pkt[0] = CMD_MB_TX; pkt[1] = (byte)req.Length;
            Array.Copy(req, 0, pkt, 2, req.Length);
            _port.Write(pkt, 0, pkt.Length); _txCount++;
        }
        catch { }
    }

    private static ushort Crc16(byte[] data, int len)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < len; i++)
        { crc ^= data[i]; for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1); }
        return crc;
    }

    // ── Control ─────────────────────────────────────────────────
    private void SendCmd(byte cmd, byte val = 0)
    {
        if (ShouldUseCan())
        {
            uint t = 0x10;
            if (uint.TryParse(TxtCanTarget.Text.Trim(), NumberStyles.HexNumber, null, out uint v)) t = v;
            SendCanFrame(CAN_ID_CMD_BASE + t, [cmd, val]);
        }
        else
        {
            byte slave = GetMbSlave();
            if (cmd == CAN_CMD_VOL) SendMbWrite(slave, 0x0003, val);
            else if (cmd == CAN_CMD_REPEAT) SendMbWrite(slave, 0x0004, val);
            else if (cmd == CAN_CMD_MONO) SendMbWrite(slave, 0x0005, val);
            else if (cmd == CAN_CMD_AUTO) SendMbWrite(slave, 0x0006, val);
            else { ushort c = cmd switch { 1=>1,2=>2,3=>3,4=>4,5=>5,_=>0 }; if (c > 0) SendMbWrite(slave, 0x0010, c); }
        }
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => SendCmd(_lastState == 1 ? CAN_CMD_PAUSE : CAN_CMD_PLAY);
    private void BtnStop_Click(object sender, RoutedEventArgs e) => SendCmd(CAN_CMD_STOP);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => SendCmd(CAN_CMD_NEXT);
    private void BtnPrev_Click(object sender, RoutedEventArgs e) => SendCmd(CAN_CMD_PREV);

    private void SliderVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtVol == null || _suppressVol) return;
        TxtVol.Text = $"{(int)SliderVol.Value}%";
        if ((DateTime.Now - _lastVolSend).TotalMilliseconds < 100) return;
        _lastVolSend = DateTime.Now;
        SendCmd(CAN_CMD_VOL, (byte)SliderVol.Value);
    }

    private void CmbRepeat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRepeat) return;
        SendCmd(CAN_CMD_REPEAT, (byte)CmbRepeat.SelectedIndex);
    }

    private void BtnMono_Click(object sender, RoutedEventArgs e)
    {
        _monoState = !_monoState;
        SendCmd(CAN_CMD_MONO, (byte)(_monoState ? 1 : 0));
        BtnMono.Content = _monoState ? "Mono: On" : "Mono: Off";
    }

    private void BtnAutoplay_Click(object sender, RoutedEventArgs e)
    {
        _autoplayState = !_autoplayState;
        SendCmd(CAN_CMD_AUTO, (byte)(_autoplayState ? 1 : 0));
        BtnAutoplay.Content = _autoplayState ? "Auto: On" : "Auto: Off";
    }

    private void BtnGoto_Click(object sender, RoutedEventArgs e)
    {
        if (!ShouldUseCan() && ushort.TryParse(TxtGoto.Text, out ushort idx))
            SendMbWrite(GetMbSlave(), 0x0011, idx);
    }

    // ── Siggen ──────────────────────────────────────────────────
    private void BtnSigStart_Click(object sender, RoutedEventArgs e)
    {
        ushort type = (ushort)(CmbSigType.SelectedIndex + 1);
        if (!ushort.TryParse(TxtSigFreq.Text, out ushort freq)) freq = 1000;
        if (ShouldUseCan())
        {
            SendCanFrame(CAN_ID_CMD_BASE + GetCanTarget(),
                [0x0A, 0x00, (byte)type, (byte)(freq & 0xFF), (byte)(freq >> 8)]);
        }
        else
        {
            byte slave = GetMbSlave();
            Task.Run(() =>
            {
                SendMbWrite(slave, 0x0013, type);  Thread.Sleep(20);
                SendMbWrite(slave, 0x0014, freq);  Thread.Sleep(20);
                SendMbWrite(slave, 0x0012, 1);
            });
        }
    }

    private void BtnSigStop_Click(object sender, RoutedEventArgs e)
    {
        if (ShouldUseCan())
            SendCanFrame(CAN_ID_CMD_BASE + GetCanTarget(), [0x0A, 0x01]);
        else
            SendMbWrite(GetMbSlave(), 0x0012, 0);
    }

    // ── RTC Sync ────────────────────────────────────────────────
    private void BtnRtcSync_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        if (ShouldUseCan())
        {
            SendCanFrame(CAN_ID_CMD_BASE + GetCanTarget(),
                [0x0B, (byte)(now.Year & 0xFF), (byte)(now.Year >> 8),
                 (byte)now.Month, (byte)now.Day, (byte)now.Hour, (byte)now.Minute, (byte)now.Second]);
        }
        else
        {
            byte slave = GetMbSlave();
            Task.Run(() =>
            {
                Thread.Sleep(50);
                SendMbWriteMultiple(slave, 0x0015,
                    [(ushort)now.Year, (ushort)now.Month, (ushort)now.Day,
                     (ushort)now.Hour, (ushort)now.Minute, (ushort)now.Second]);
            });
        }
        Log($"RTC sync: {now:yyyy-MM-dd HH:mm:ss}");
    }

    // ── Raw Register ────────────────────────────────────────────
    private async void BtnRegRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort addr = Convert.ToUInt16(TxtRegAddr.Text, 16);
            ushort count = ushort.Parse(TxtRegCount.Text);
            SendMbRead(GetMbSlave(), addr, count);
            TxtRegResult.Text = "sent...";
        }
        catch (Exception ex) { TxtRegResult.Text = ex.Message; }
    }

    private void BtnRegWrite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ushort addr = Convert.ToUInt16(TxtRegAddr.Text, 16);
            ushort val = Convert.ToUInt16(TxtRegVal.Text, 16);
            SendMbWrite(GetMbSlave(), addr, val);
            TxtRegResult.Text = $"Write 0x{val:X4} → 0x{addr:X4}";
        }
        catch (Exception ex) { TxtRegResult.Text = ex.Message; }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) { TxtLog.Clear(); _txCount = 0; _rxCount = 0; }

    private void BtnTestFault_Click(object sender, RoutedEventArgs e)
    {
        if (_port == null || !_port.IsOpen) return;
        try
        {
            _port.Write(new byte[] { 0xFE, 0xFE, 0x11 }, 0, 3);
            Log("Test fault written — click Fault Log to read");
        }
        catch { }
    }

    private void TrafficTx(string msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ChkTrafficRun.IsChecked != true) return;
            _trafficTx++;
            TxtTrafficTx.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            TxtTrafficTx.ScrollToEnd();
            TxtTrafficCount.Text = $" TX:{_trafficTx} RX:{_trafficRx}";
        });
    }

    private void TrafficRx(string msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ChkTrafficRun.IsChecked != true) return;
            _trafficRx++;
            TxtTrafficRx.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            TxtTrafficRx.ScrollToEnd();
            TxtTrafficCount.Text = $" TX:{_trafficTx} RX:{_trafficRx}";
        });
    }

    // ── Sensor Test ──────────────────────────────────────────
    private DateTime _lastAnalogSend = DateTime.MinValue;

    private void SendSensorFrame(byte flags)
    {
        uint hub = (uint)(CmbTestHub.SelectedIndex + 1);
        uint canId = 0x500 + hub;
        byte.TryParse(TxtTestZone.Text, out byte zone);
        byte type = (byte)(CmbTestType.SelectedIndex + 1);
        byte.TryParse(TxtTestValue.Text, out byte value);
        SendCanFrame(canId, [zone, type, value, flags]);
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        string fstr = (flags & 3) switch { 1 => "RISE", 2 => "FALL", 3 => "BOTH", _ => "?" };
        TxtTestLog.AppendText($"[{ts}] H{hub} zone={zone} type={CmbTestType.Text} val={value} {fstr}\n");
        TxtTestLog.ScrollToEnd();
    }

    private void BtnTestRising_Click(object sender, RoutedEventArgs e) => SendSensorFrame(0x01);
    private void BtnTestFalling_Click(object sender, RoutedEventArgs e) => SendSensorFrame(0x02);
    private void BtnTestBoth_Click(object sender, RoutedEventArgs e) => SendSensorFrame(0x03);

    private void SliderTestAnalog_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtTestAnalogVal != null) TxtTestAnalogVal.Text = $"{(int)SliderTestAnalog.Value}";
    }

    private void BtnTestAnalogSend_Click(object sender, RoutedEventArgs e)
    {
        uint hub = (uint)(CmbTestHub.SelectedIndex + 1);
        byte val = (byte)SliderTestAnalog.Value;
        byte.TryParse(TxtTestZone.Text, out byte zone);
        SendCanFrame(0x500 + hub, [zone, 0x05, val, 0x01]);
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtTestLog.AppendText($"[{ts}] H{hub} zone={zone} analog={val}\n");
        TxtTestLog.ScrollToEnd();
    }

    // ── Remote Test ─────────────────────────────────────────
    private void SendRemoteCmd(byte cmd, byte val = 0)
    {
        uint hub = (uint)(CmbRemoteHub.SelectedIndex + 1);
        SendCanFrame(0x500 + hub, [cmd, val]);
        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        string[] names = ["?","Play","Stop","Next","Prev","Pause","Vol"];
        string name = cmd <= 6 ? names[cmd] : $"0x{cmd:X2}";
        TxtTestLog.AppendText($"[{ts}] Remote H{hub} {name} val={val}\n");
        TxtTestLog.ScrollToEnd();
    }

    private void BtnRemotePlay_Click(object sender, RoutedEventArgs e) => SendRemoteCmd(0x01);
    private void BtnRemoteStop_Click(object sender, RoutedEventArgs e) => SendRemoteCmd(0x02);
    private void BtnRemoteNext_Click(object sender, RoutedEventArgs e) => SendRemoteCmd(0x03);
    private void BtnRemotePrev_Click(object sender, RoutedEventArgs e) => SendRemoteCmd(0x04);
    private void BtnRemotePause_Click(object sender, RoutedEventArgs e) => SendRemoteCmd(0x05);
    private void SliderRemoteVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtRemoteVolVal == null) return;
        TxtRemoteVolVal.Text = $"{(int)SliderRemoteVol.Value}%";
        if (_port == null || !_port.IsOpen) return;
        if ((DateTime.Now - _lastAnalogSend).TotalMilliseconds < 100) return;
        _lastAnalogSend = DateTime.Now;
        SendRemoteCmd(0x06, (byte)SliderRemoteVol.Value);
    }

    private void BtnTrafficClear_Click(object sender, RoutedEventArgs e)
    {
        TxtTrafficTx.Clear(); TxtTrafficRx.Clear();
        _trafficTx = 0; _trafficRx = 0;
        TxtTrafficCount.Text = " TX:0 RX:0";
    }

    // ── Register Map ────────────────────────────────────────────
    public class RegEntry { public string Addr { get; set; } = ""; public string Name { get; set; } = ""; public string Value { get; set; } = "—"; public string Dec { get; set; } = ""; public string RW { get; set; } = ""; public string Desc { get; set; } = ""; public ushort RawAddr; }

    private void UpdateRegMap(ushort startAddr, ushort[] regs)
    {
        if (!_regMapInit) return;
        for (int i = 0; i < regs.Length; i++)
        {
            ushort addr = (ushort)(startAddr + i);
            foreach (var reg in _regMap)
            {
                if (reg.RawAddr == addr)
                {
                    reg.Value = $"0x{regs[i]:X4}";
                    reg.Dec = $"{regs[i]}";
                    break;
                }
            }
        }
        DgRegMap.Items.Refresh();
    }

    private readonly List<RegEntry> _regMap = new()
    {
        new() { RawAddr=0x0000, Addr="0x0000", Name="STATE",      RW="RO", Desc="0=stop 1=play 3=pause" },
        new() { RawAddr=0x0001, Addr="0x0001", Name="TRACK",      RW="RO", Desc="track index (0-based)" },
        new() { RawAddr=0x0002, Addr="0x0002", Name="TRACK_COUNT", RW="RO", Desc="total tracks" },
        new() { RawAddr=0x0003, Addr="0x0003", Name="VOLUME",     RW="RW", Desc="0-100" },
        new() { RawAddr=0x0004, Addr="0x0004", Name="REPEAT",     RW="RW", Desc="0=All 1=One 2=Off 3=Single 4=Random" },
        new() { RawAddr=0x0005, Addr="0x0005", Name="MONO",       RW="RW", Desc="0=stereo 1=mono" },
        new() { RawAddr=0x0006, Addr="0x0006", Name="AUTOPLAY",   RW="RW", Desc="0=off 1=on" },
        new() { RawAddr=0x0007, Addr="0x0007", Name="SD_OK",      RW="RO", Desc="0=no SD 1=OK" },
        new() { RawAddr=0x0008, Addr="0x0008", Name="USB_OK",     RW="RO", Desc="always 1" },
        new() { RawAddr=0x0010, Addr="0x0010", Name="COMMAND",    RW="WO", Desc="1=play 2=stop 3=next 4=prev 5=pause" },
        new() { RawAddr=0x0011, Addr="0x0011", Name="GOTO_INDEX", RW="WO", Desc="track index" },
        new() { RawAddr=0x0012, Addr="0x0012", Name="SIGGEN_CMD", RW="RW", Desc="0=stop 1=start" },
        new() { RawAddr=0x0013, Addr="0x0013", Name="SIGGEN_TYPE",RW="RW", Desc="1=Sin 2=Sq 3=Tri 4=Saw 5=White 6=Pink" },
        new() { RawAddr=0x0014, Addr="0x0014", Name="SIGGEN_FREQ",RW="RW", Desc="1-20000 Hz" },
        new() { RawAddr=0x0015, Addr="0x0015", Name="RTC_YEAR",   RW="RW", Desc="e.g. 2026" },
        new() { RawAddr=0x0016, Addr="0x0016", Name="RTC_MONTH",  RW="RW", Desc="1-12" },
        new() { RawAddr=0x0017, Addr="0x0017", Name="RTC_DAY",    RW="RW", Desc="1-31" },
        new() { RawAddr=0x0018, Addr="0x0018", Name="RTC_HOUR",   RW="RW", Desc="0-23" },
        new() { RawAddr=0x0019, Addr="0x0019", Name="RTC_MIN",    RW="RW", Desc="0-59" },
        new() { RawAddr=0x001A, Addr="0x001A", Name="RTC_SEC",    RW="WO", Desc="0-59 (write triggers set)" },
        new() { RawAddr=0x0020, Addr="0x0020", Name="UPTIME",     RW="RO", Desc="seconds low 16-bit" },
        new() { RawAddr=0x0021, Addr="0x0021", Name="TEMP_X10",   RW="RO", Desc="temp x10 (325=32.5C)" },
        new() { RawAddr=0x0022, Addr="0x0022", Name="FW_MAJOR",   RW="RO", Desc="firmware major" },
        new() { RawAddr=0x0023, Addr="0x0023", Name="FW_MINOR",   RW="RO", Desc="firmware minor" },
        new() { RawAddr=0x0024, Addr="0x0024", Name="SLAVE_ADDR", RW="RO", Desc="Modbus slave address" },
        new() { RawAddr=0x0025, Addr="0x0025", Name="HEAP_FREE",  RW="RO", Desc="free heap / 16" },
        new() { RawAddr=0x0026, Addr="0x0026", Name="SAMPLE_RATE",RW="RO", Desc="sample rate / 100" },
        new() { RawAddr=0x0027, Addr="0x0027", Name="BAUDRATE",   RW="RW", Desc="baud index 0-6" },
        new() { RawAddr=0x0028, Addr="0x0028", Name="UPTIME_HI",  RW="RO", Desc="seconds high 16-bit" },
    };

    private bool _regMapInit;
    private volatile bool _regMapBusy;

    private void InitRegMap()
    {
        if (_regMapInit) return;
        DgRegMap.ItemsSource = _regMap;
        _regMapInit = true;
    }

    private CancellationTokenSource? _regLiveCts;

    private void BtnRegMapRead_Click(object sender, RoutedEventArgs e)
    {
        InitRegMap();
        if (ChkRegLive.IsChecked == true)
        {
            if (_regLiveCts != null) { _regLiveCts.Cancel(); _regLiveCts = null; ChkRegLive.IsChecked = false; TxtRegMapStatus.Text = "Stopped"; return; }
            _regLiveCts = new CancellationTokenSource();
            var ct = _regLiveCts.Token;
            Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    DoRegMapRead();
                    Thread.Sleep(_pollMs);
                }
            });
            TxtRegMapStatus.Text = "Live...";
            return;
        }
        if (_port == null || !_port.IsOpen) return;
        TxtRegMapStatus.Text = "Live — values update from poll";
    }

    private void DoRegMapRead()
    {
        try
        {
            _regMapBusy = true;
            byte slave = GetMbSlave();
            int gap = _gapMs;

            ushort[]? b0 = ReadMbBlock(slave, 0x0000, 9, gap);
            ushort[]? b2 = ReadMbBlock(slave, 0x0012, 3, gap);
            ushort[]? b3 = ReadMbBlock(slave, 0x0015, 6, gap);
            ushort[]? b4 = ReadMbBlock(slave, 0x0020, 9, gap);
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var reg in _regMap)
                {
                    ushort addr = reg.RawAddr;
                    ushort? val = null;
                    if (addr <= 0x0008 && b0 != null && addr < b0.Length) val = b0[addr];
                    else if (addr >= 0x0012 && addr <= 0x0014 && b2 != null && addr - 0x0012 < b2.Length) val = b2[addr - 0x0012];
                    else if (addr >= 0x0015 && addr <= 0x001A && b3 != null && addr - 0x0015 < b3.Length) val = b3[addr - 0x0015];
                    else if (addr >= 0x0020 && addr <= 0x0028 && b4 != null && addr - 0x0020 < b4.Length) val = b4[addr - 0x0020];
                    if (val.HasValue) { reg.Value = $"0x{val:X4}"; reg.Dec = $"{val}"; }
                }
                DgRegMap.Items.Refresh();
                TxtRegMapStatus.Text = $"OK ({DateTime.Now:HH:mm:ss})";
            });
        }
        catch (Exception ex) { Dispatcher.BeginInvoke(() => TxtRegMapStatus.Text = $"Error: {ex.Message}"); }
        finally { _regMapBusy = false; }
    }

    private ushort[]? ReadMbBlock(byte slave, ushort start, ushort count, int gap)
    {
        try
        {
            SendMbRead(slave, start, count);
            Thread.Sleep(gap + 50);
            // Response comes via RxLoop → ParseMbResponse
            // For reg map, read directly
            if (_port == null || _port.BytesToRead < 2) return null;
            byte[] hdr = new byte[3];
            int b = _port.ReadByte();
            if (b != CMD_MB_RX) return null;
            int len = _port.ReadByte();
            if (len <= 0) return null;
            byte[] resp = new byte[len];
            int read = 0;
            var start_t = DateTime.Now;
            while (read < len && (DateTime.Now - start_t).TotalMilliseconds < 200)
            {
                if (_port.BytesToRead > 0) read += _port.Read(resp, read, len - read);
                else Thread.Sleep(1);
            }
            if (resp[1] != 0x03) return null;
            int bytes = resp[2];
            int n = bytes / 2;
            ushort[] regs = new ushort[n];
            for (int i = 0; i < n && (3 + i * 2 + 1) < len; i++)
                regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[3 + i * 2 + 1]);
            return regs;
        }
        catch { return null; }
    }

    protected override void OnClosed(EventArgs e) { SaveWindowState(); SaveSettings(); Disconnect(); base.OnClosed(e); }
}
