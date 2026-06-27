using System;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace STM32GatewayGui;

public partial class MainWindow : Window
{
    const byte CMD_CAN_TX = 0x01, CMD_CAN_RX = 0x02;
    const byte CMD_MB_TX  = 0x03, CMD_MB_RX  = 0x04;

    const byte CAN_CMD_PLAY = 1, CAN_CMD_STOP = 2, CAN_CMD_NEXT = 3;
    const byte CAN_CMD_PREV = 4, CAN_CMD_PAUSE = 5, CAN_CMD_VOL = 6;
    const byte CAN_CMD_GET  = 0x10;

    const uint CAN_ID_CMD_BASE = 0x100, CAN_ID_NAME_BASE = 0x200, CAN_ID_STATUS_BASE = 0x300;

    static readonly string[] StateNames = ["Stop", "Play", "Error", "Pause"];

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private int _txCount, _rxCount, _lastState, _pollCycle;
    private bool _suppressVol;
    private DateTime _lastVolSend = DateTime.MinValue;
    private readonly string[] _nameFrames = new string[20];

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
    private static readonly string WindowPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "window.txt");

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowState();
        SizeChanged += (_, _) => SaveWindowState();
        LocationChanged += (_, _) => SaveWindowState();
        StateChanged += (_, _) => SaveWindowState();
        RefreshPorts();
        LoadSettings();
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
            File.WriteAllText(WindowPath,
                $"{l:F0},{t:F0},{w:F0},{h:F0},{(WindowState == WindowState.Maximized ? 1 : 0)}");
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
        try
        {
            string port = CmbPort.SelectedItem?.ToString() ?? "";
            string bus = RbCan.IsChecked == true ? "CAN" : "MB";
            File.WriteAllText(SettingsPath, $"{port},{TxtCanTarget.Text},{TxtMbSlave.Text},{bus}");
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
            if (p.Length >= 4) { RbCan.IsChecked = p[3] == "CAN"; RbModbus.IsChecked = p[3] == "MB"; }
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
            _port = new SerialPort(CmbPort.SelectedItem.ToString()!, 115200, Parity.None, 8, StopBits.One)
            { ReadTimeout = 500, WriteTimeout = 500 };
            _port.Open();
            _cts = new CancellationTokenSource();
            Task.Run(() => RxLoop(_cts.Token));
            Task.Run(() => PollLoop(_cts.Token));
            BtnConnect.Content = "Disconnect";
            TxtStatus.Text = "Connected";
            TxtStatus.Foreground = FindResource("CatGreen") as System.Windows.Media.Brush;
            SaveSettings();
            Log("Connected");
        }
        catch (Exception ex) { Log($"Failed: {ex.Message}"); }
    }

    private void Disconnect()
    {
        _cts?.Cancel();
        _port?.Close(); _port?.Dispose(); _port = null;
        BtnConnect.Content = "Connect";
        TxtStatus.Text = "Disconnected";
        TxtStatus.Foreground = FindResource("CatRed") as System.Windows.Media.Brush;
        Log("Disconnected");
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

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

                if (b == CMD_CAN_RX)
                {
                    int n = _port.Read(buf, 0, 3);
                    if (n < 3) continue;
                    uint id = (uint)((buf[0] << 8) | buf[1]);
                    int dlc = buf[2]; if (dlc > 8) dlc = 8;
                    if (dlc > 0) _port.Read(buf, 3, dlc);
                    byte[] data = new byte[dlc];
                    Array.Copy(buf, 3, data, 0, dlc);
                    Dispatcher.BeginInvoke(() => { _rxCount++; ParseCanFrame(id, data); });
                }
                else if (b == CMD_MB_RX)
                {
                    int len = _port.ReadByte();
                    if (len <= 0 || len > 250) continue;
                    byte[] resp = new byte[len];
                    int read = 0;
                    while (read < len) read += _port.Read(resp, read, len - read);
                    Dispatcher.BeginInvoke(() => { _rxCount++; ParseMbResponse(resp, len); });
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
                bool useCan = true;
                Dispatcher.Invoke(() => useCan = RbCan.IsChecked == true);

                if (useCan)
                {
                    uint target = GetCanTarget();
                    SendCanFrame(CAN_ID_CMD_BASE + target, [CAN_CMD_GET, 0]);
                }
                else
                {
                    byte slave = GetMbSlave();
                    SendMbRead(slave, 0x0000, 9);
                    Thread.Sleep(50);
                    SendMbRead(slave, 0x0020, 8);
                    Thread.Sleep(50);
                    if (_pollCycle % 2 == 0)
                        SendMbRead(slave, 0x0100, 16);
                    else
                        SendMbRead(slave, 0x0015, 6);
                    _pollCycle++;
                }
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    // ── Parse CAN frames ────────────────────────────────────────
    private void ParseCanFrame(uint id, byte[] data)
    {
        if (id >= CAN_ID_STATUS_BASE && id <= CAN_ID_STATUS_BASE + 0x7F && data.Length >= 7)
        {
            int state = data[0];
            _lastState = state;
            TxtState.Text = $"State: {(state < StateNames.Length ? StateNames[state] : "?")}";
            BtnPlayPause.Content = state == 1 ? "⏸ Pause" : "▶ Play";
            TxtTrack.Text = $"Track: {data[1] + 1}";
            _suppressVol = true;
            TxtVolume.Text = $"Volume: {data[2]}%";
            SliderVol.Value = data[2]; TxtVol.Text = $"{data[2]}%";
            _suppressVol = false;
            TxtSample.Text = $"Sample: {data[4]}kHz";
            TxtTemp.Text = $"Temp: {data[5]}°C";
            TxtGroup.Text = $"Group: {(char)('A' + data[6])}";
            TxtBus.Text = "Bus: OK";
        }
        if (id >= CAN_ID_NAME_BASE && id <= CAN_ID_NAME_BASE + 0x7F && data.Length >= 1)
        {
            int seq = data[0];
            if (seq == 0) Array.Clear(_nameFrames, 0, _nameFrames.Length);
            if (seq < _nameFrames.Length)
            {
                var sb = new StringBuilder();
                for (int i = 1; i < data.Length; i++) { if (data[i] == 0) break; sb.Append((char)data[i]); }
                _nameFrames[seq] = sb.ToString();
                sb.Clear();
                for (int i = 0; i < _nameFrames.Length; i++) { if (_nameFrames[i] == null) break; sb.Append(_nameFrames[i]); }
                TxtTrackName.Text = $"♪ {sb}";
            }
        }
    }

    // ── Parse Modbus response ───────────────────────────────────
    private void ParseMbResponse(byte[] resp, int len)
    {
        if (len < 5 || resp[1] != 0x03) return;
        int bytes = resp[2];
        int count = bytes / 2;
        ushort[] regs = new ushort[count];
        for (int i = 0; i < count && (3 + i * 2 + 1) < len; i++)
            regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[3 + i * 2 + 1]);

        if (count == 9 && regs[0] <= 3)
        {
            int state = regs[0];
            _lastState = state;
            TxtState.Text = $"State: {(state < StateNames.Length ? StateNames[state] : "?")}";
            BtnPlayPause.Content = state == 1 ? "⏸ Pause" : "▶ Play";
            TxtTrack.Text = $"Track: {regs[1] + 1}/{regs[2]}";
            _suppressVol = true;
            TxtVolume.Text = $"Volume: {regs[3]}%";
            SliderVol.Value = regs[3]; TxtVol.Text = $"{regs[3]}%";
            _suppressVol = false;
            TxtRepeat.Text = $"Repeat: {regs[4]}";
            TxtBus.Text = $"SD: {(regs[7] != 0 ? "OK" : "—")}";
        }
        else if (count == 8)
        {
            TxtTemp.Text = $"Temp: {regs[1] / 10.0:F1}°C";
            TxtSample.Text = $"{regs[6] * 100 / 1000}kHz";
        }
        else if (count == 16)
        {
            var sb = new StringBuilder();
            foreach (var r in regs)
            {
                char hi = (char)(r >> 8), lo = (char)(r & 0xFF);
                if (hi != 0) sb.Append(hi);
                if (lo != 0) sb.Append(lo);
            }
            string name = sb.ToString().TrimEnd('\0');
            if (name.Length > 0) TxtTrackName.Text = $"♪ {name}";
        }
        else if (count == 6 && regs[0] >= 2020)
        {
            TxtGroup.Text = $"RTC: {regs[3]:D2}:{regs[4]:D2}:{regs[5]:D2}";
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
            byte[] pkt = new byte[10];
            pkt[0] = CMD_MB_TX; pkt[1] = 8;
            Array.Copy(req, 0, pkt, 2, 8);
            _port.Write(pkt, 0, 10); _txCount++;
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
            byte[] pkt = new byte[10];
            pkt[0] = CMD_MB_TX; pkt[1] = 8;
            Array.Copy(req, 0, pkt, 2, 8);
            _port.Write(pkt, 0, 10); _txCount++;
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
        if (RbCan.IsChecked == true)
        {
            uint target = 0x10;
            if (uint.TryParse(TxtCanTarget.Text.Trim(), NumberStyles.HexNumber, null, out uint v)) target = v;
            SendCanFrame(CAN_ID_CMD_BASE + target, [cmd, val]);
        }
        else
        {
            byte slave = GetMbSlave();
            if (cmd == CAN_CMD_VOL) SendMbWrite(slave, 0x0003, val);
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

    // ── Raw send ────────────────────────────────────────────────
    private byte[] ParseHexData()
    {
        var parts = TxtSendData.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var data = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) data[i] = byte.Parse(parts[i], NumberStyles.HexNumber);
        return data;
    }

    private void BtnSendCan_Click(object sender, RoutedEventArgs e)
    {
        try { uint id = uint.Parse(TxtSendId.Text.Trim(), NumberStyles.HexNumber); SendCanFrame(id, ParseHexData()); Log($"CAN TX 0x{id:X3}"); }
        catch (Exception ex) { Log($"Error: {ex.Message}"); }
    }

    private void BtnSendMb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = ParseHexData();
            if (_port == null) return;
            byte[] pkt = new byte[2 + data.Length];
            pkt[0] = CMD_MB_TX; pkt[1] = (byte)data.Length;
            Array.Copy(data, 0, pkt, 2, data.Length);
            _port.Write(pkt, 0, pkt.Length); Log($"MB TX [{data.Length}]");
        }
        catch (Exception ex) { Log($"Error: {ex.Message}"); }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) { TxtLog.Clear(); _txCount = 0; _rxCount = 0; }

    protected override void OnClosed(EventArgs e) { SaveWindowState(); SaveSettings(); Disconnect(); base.OnClosed(e); }
}
