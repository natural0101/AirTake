using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace AirTake.Moblin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.RunAsync(args.LastOrDefault() ?? "qa").GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "AirTake", MessageBoxButtons.OK, MessageBoxIcon.Error);
        try
        {
            using var window = new CaptureWindow(args.Contains("--smoke-test"));
            if (args.Contains("--smoke-test"))
            {
                window.Show(); Application.DoEvents();
                using var image = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(args.Length > 1 ? args[1] : "desktop.png", System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            }
            Application.Run(window);
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "airtake-error.txt"), e.ToString());
            if (!args.Contains("--smoke-test")) MessageBox.Show(e.Message, "AirTake — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed record NetworkChoice(string Address, string Name)
{
    public override string ToString() => $"{Address} · {Name}";
}

internal sealed class CaptureWindow : Form
{
    private static readonly Color Background = Color.FromArgb(17, 21, 31);
    private static readonly Color Surface = Color.FromArgb(27, 33, 47);
    private static readonly Color Foreground = Color.FromArgb(237, 240, 247);
    private static readonly Color Muted = Color.FromArgb(170, 182, 204);
    private static readonly Color Accent = Color.FromArgb(132, 103, 250);
    private readonly ComboBox network = Combo();
    private readonly ComboBox microphone = Combo();
    private readonly ComboBox fps = Combo();
    private readonly NumericUpDown port = Number(1024, 65535, 9000);
    private readonly NumericUpDown latency = Number(200, 10000, 2000);
    private readonly NumericUpDown bitrate = Number(10, 200, 120);
    private readonly NumericUpDown offset = Number(-10000, 10000, 0);
    private readonly TextBox directory = TextInput();
    private readonly TextBox password = TextInput();
    private readonly CheckBox recordMic = Check("Записывать микрофон Fifine", true);
    private readonly CheckBox reconnect = Check("Переподключаться после обрыва", true);
    private readonly CheckBox preview = Check("Предпросмотр в отдельном окне (нагрузка на ПК)", false);
    private readonly CheckBox mp4 = Check("После Stop создать MP4; исходный MKV оставить", false);
    private readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 8, 0, 8) };
    private readonly TextBox stream = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Muted };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Muted };
    private readonly Label status = Label("Проверка компонентов…", 13, true);
    private readonly Label counters = Label("Файл записывается на компьютер · без OBS", 10);
    private readonly Button start = Button("●  НАЧАТЬ ЗАПИСЬ", true);
    private readonly Button stop = Button("ОСТАНОВИТЬ");
    private readonly Panel settingsPanel = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Surface, Padding = new Padding(18) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private CaptureSettings saved = new();
    private CaptureSession? session;
    private bool ready;
    private bool closing;
    private readonly bool smoke;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTake", "moblin-settings.json");

    public CaptureWindow(bool smoke)
    {
        this.smoke = smoke;
        Text = "AirTake · Moblin → Windows";
        BackColor = Background; ForeColor = Foreground; Font = new Font("Segoe UI", 10);
        Width = 1180; Height = 940; MinimumSize = new Size(1040, 740); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 3, BackColor = Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(Label("AIRTAKE", 25, true)); header.Controls.Add(Label("MOBLIN  →  WI-FI  →  WINDOWS SSD", 10));
        root.Controls.Add(header, 0, 0); root.SetColumnSpan(header, 2);
        root.Controls.Add(settingsPanel, 0, 1);
        settingsPanel.Margin = new Padding(0, 0, 12, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Surface };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(fields, "Сеть компьютера", network);
        AddRow(fields, "UDP-порт", port);
        AddRow(fields, "Буфер SRT, мс", latency);
        password.UseSystemPasswordChar = true;
        AddRow(fields, "Пароль подключения", password);
        var pairing = Button("Обновить QR / пресет Moblin"); pairing.Click += (_, _) => TryAction(UpdateQr);
        AddFull(fields, pairing, 44);
        AddFull(fields, Label("Пресет импортируется в Moblin; это не удалённое управление камерой.", 9), 46);
        fps.Items.AddRange([30, 60, 120]);
        AddRow(fields, "Пресет 4K, FPS", fps);
        AddRow(fields, "Битрейт, Мбит/с", bitrate);
        AddFull(fields, recordMic, 38);
        AddRow(fields, "Микрофон", microphone);
        var refresh = Button("Обновить список микрофонов"); refresh.Click += async (_, _) => await RefreshMicrophonesAsync();
        AddFull(fields, refresh, 40);
        AddRow(fields, "Сдвиг звука, мс", offset);
        AddFull(fields, Label("Синхронизация: калибровка хлопком. «+» — звук позже, «−» — раньше.", 9), 42);
        var browse = Button("…"); browse.Width = 40;
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = directory.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) directory.Text = dialog.SelectedPath; };
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
        pathRow.Controls.Add(directory, 0, 0); pathRow.Controls.Add(browse, 1, 0);
        AddRow(fields, "Папка записи", pathRow);
        AddFull(fields, reconnect, 36); AddFull(fields, preview, 36); AddFull(fields, mp4, 36);
        var firewall = Button("Разрешить приём в брандмауэре Windows"); firewall.Click += (_, _) => TryAction(ConfigureFirewall);
        AddFull(fields, firewall, 42);
        settingsPanel.Controls.Add(fields);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Surface, ColumnCount = 1, RowCount = 6, Margin = new Padding(0) };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        right.Controls.Add(Label("ПОДКЛЮЧЕНИЕ MOBLIN", 12, true), 0, 0);
        right.Controls.Add(qr, 0, 1);
        right.Controls.Add(Label("1. iPhone и ПК — в одной локальной сети.\n2. В Moblin импортируйте этот QR / пресет.\n3. Нажмите запись здесь, затем Go Live в Moblin.\n4. 120 FPS в Moblin — экспериментальный режим.", 10), 0, 2);
        var links = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var copyLink = Button("Копировать SRT"); copyLink.AutoSize = true; copyLink.Click += (_, _) => TryAction(() => Clipboard.SetText(ReadSettings(false).CallerUrl));
        var copyPreset = Button("Копировать пресет"); copyPreset.AutoSize = true; copyPreset.Click += (_, _) => TryAction(() => Clipboard.SetText(ReadSettings(false).MoblinUrl));
        links.Controls.Add(copyLink); links.Controls.Add(copyPreset); right.Controls.Add(links, 0, 3);
        stream.Text = "Входящий формат появится после подключения.\r\n4K / 120 в пресете — запрос, а не подтверждённый результат.";
        right.Controls.Add(stream, 0, 4); right.Controls.Add(log, 0, 5); root.Controls.Add(right, 1, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Padding = new Padding(0, 14, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var width in new[] { 190f, 150f, 140f }) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(start, 1, 0); bottom.Controls.Add(stop, 2, 0);
        var open = Button("Открыть папку"); open.Click += (_, _) => TryAction(() => {
            var path = session?.SessionDirectory; if (string.IsNullOrEmpty(path)) path = directory.Text;
            System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
        bottom.Controls.Add(open, 3, 0); bottom.Controls.Add(counters, 0, 1); bottom.SetColumnSpan(counters, 4);
        root.Controls.Add(bottom, 0, 2); root.SetColumnSpan(bottom, 2); Controls.Add(root);
        start.Enabled = false; stop.Enabled = false;
        start.Click += async (_, _) => await StartAsync();
        stop.Click += async (_, _) => { stop.Enabled = false; if (session is not null) await session.StopAsync(); Tick(); };
        recordMic.CheckedChanged += (_, _) => { microphone.Enabled = recordMic.Checked; offset.Enabled = recordMic.Checked; };
        LoadSettings();
        timer.Tick += (_, _) => Tick(); timer.Start();
        Shown += async (_, _) => {
            if (smoke) { status.Text = "Готово к подключению"; return; }
            try { await MediaTools.CheckAsync(); await RefreshMicrophonesAsync(); ready = true; status.Text = "Готово — импортируйте пресет в Moblin"; }
            catch (Exception e) { AddLog(e.Message); status.Text = "Ошибка компонентов"; }
            Tick();
        };
        FormClosing += async (_, e) => {
            if (closing) return;
            if (session?.Running == true)
            {
                e.Cancel = true; closing = true; status.Text = "Остановка и сохранение…";
                await session.StopAsync(); Close();
            }
        };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); qr.Image?.Dispose(); };
    }

    private void LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) saved = JsonSerializer.Deserialize<CaptureSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) { AddLog("Настройки не прочитаны: " + e.Message); }
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                network.Items.Add(new NetworkChoice(address.Address.ToString(), adapter.Name));
        if (network.Items.Count == 0) network.Items.Add(new NetworkChoice("127.0.0.1", "Нет локальной сети"));
        network.SelectedIndex = 0;
        for (int i = 0; i < network.Items.Count; i++) if (((NetworkChoice)network.Items[i]).Address == saved.Address) network.SelectedIndex = i;
        port.Value = Math.Clamp(saved.Port, 1024, 65535); latency.Value = Math.Clamp(saved.LatencyMs, 200, 10000);
        bitrate.Value = Math.Clamp(saved.BitrateMbps, 10, 200); offset.Value = Math.Clamp(saved.MicrophoneOffsetMs, -10000, 10000);
        fps.SelectedItem = saved.TargetFps is 30 or 60 or 120 ? saved.TargetFps : 120;
        directory.Text = saved.Directory; password.Text = saved.Passphrase;
        recordMic.Checked = saved.RecordMicrophone; reconnect.Checked = saved.AutoReconnect; preview.Checked = saved.Preview; mp4.Checked = saved.ExportMp4;
        TryAction(UpdateQr);
    }

    private CaptureSettings ReadSettings(bool recording)
    {
        var value = new CaptureSettings {
            Address = ((NetworkChoice?)network.SelectedItem)?.Address ?? "127.0.0.1", Port = (int)port.Value,
            LatencyMs = (int)latency.Value, BitrateMbps = (int)bitrate.Value, TargetFps = (int)(fps.SelectedItem ?? 120),
            Passphrase = password.Text.Trim(), Directory = directory.Text.Trim(),
            RecordMicrophone = recording && recordMic.Checked, Microphone = ((MicrophoneDevice?)microphone.SelectedItem)?.Id ?? "",
            MicrophoneOffsetMs = (int)offset.Value, AutoReconnect = reconnect.Checked, Preview = preview.Checked, ExportMp4 = mp4.Checked
        };
        value.Validate();
        return value;
    }

    private void Save(CaptureSettings value)
    {
        var persisted = JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(value))!;
        persisted.RecordMicrophone = recordMic.Checked;
        persisted.Microphone = ((MicrophoneDevice?)microphone.SelectedItem)?.Id ?? saved.Microphone;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true }));
        saved = persisted;
    }

    private void UpdateQr()
    {
        var value = ReadSettings(false);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value.MoblinUrl, QRCodeGenerator.ECCLevel.M);
        using var code = new QRCode(data);
        var image = code.GetGraphic(6, Color.Black, Color.White, true);
        var old = qr.Image; qr.Image = image; old?.Dispose();
        if (!smoke) Save(value);
    }

    private async Task RefreshMicrophonesAsync()
    {
        if (session?.Running == true) return;
        try
        {
            var selected = ((MicrophoneDevice?)microphone.SelectedItem)?.Id ?? saved.Microphone;
            var devices = await MediaTools.MicrophonesAsync();
            microphone.Items.Clear();
            foreach (var device in devices) microphone.Items.Add(device);
            var choice = devices.FirstOrDefault(d => d.Id == selected) ?? devices.FirstOrDefault(d => d.Name.Contains("fifine", StringComparison.OrdinalIgnoreCase));
            if (choice is not null) microphone.SelectedItem = choice;
            AddLog(devices.Count == 0 ? "Микрофоны не найдены. Проверьте USB и доступ Windows к микрофону." : $"Найдено микрофонов: {devices.Count}. Выберите Fifine.");
        }
        catch (Exception e) { AddLog(e.Message); }
    }

    private async Task StartAsync()
    {
        start.Enabled = false;
        try
        {
            var options = ReadSettings(true);
            if (options.Address == "127.0.0.1") throw new InvalidOperationException("Выберите сетевой адаптер с адресом, доступным iPhone.");
            await MediaTools.CheckAsync(); UpdateQr(); Save(options);
            session = new CaptureSession(options); session.Log += AddLog; session.Start();
            AddLog("Приём включён. Теперь нажмите Go Live в Moblin. Микрофон телефона в файл не добавляется.");
        }
        catch (Exception e) { AddLog(e.Message); MessageBox.Show(this, e.Message, "AirTake", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        Tick();
    }

    private void Tick()
    {
        if (IsDisposed) return;
        var active = session?.Running == true;
        settingsPanel.Enabled = !active;
        start.Enabled = ready && !active; stop.Enabled = active;
        if (session is not null)
        {
            status.Text = session.State;
            if (session.Frames > 0) stream.Text = session.Stream;
            counters.Text = $"Текущая часть: {session.Bytes / 1000000000.0:F2} ГБ  ·  {TimeSpan.FromSeconds(session.Seconds):hh\\:mm\\:ss}  ·  {session.Frames:N0} кадров по FFmpeg  ·  Завершённых частей: {session.Files.Length}";
        }
        else
        {
            var free = MediaTools.FreeBytes(directory.Text);
            counters.Text = $"Пресет: {(int)bitrate.Value * 0.45:F1} ГБ/час видео (+ звук)  ·  Свободно: {(free < 0 ? "не определено" : $"{free / 1000000000.0:F1} ГБ")}  ·  Кодек и FPS не подменяются";
        }
    }

    private void ConfigureFirewall()
    {
        var value = ReadSettings(false);
        var executable = MediaTools.Ffmpeg.Replace("'", "''");
        var name = $"AirTake Moblin SRT {value.Port}";
        var command = $"$ErrorActionPreference='Stop'; Get-NetFirewallRule -DisplayName '{name}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName '{name}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort {value.Port} -Profile Private -RemoteAddress LocalSubnet -Program '{executable}' | Out-Null";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -EncodedCommand " + encoded) { UseShellExecute = true, Verb = "runas" });
        AddLog("Запрошено правило только для частной локальной сети. Профиль вашего Wi-Fi в Windows должен быть «Частная сеть».");
    }

    private void AddLog(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => AddLog(line))); } catch (InvalidOperationException) { } return; }
        if (log.TextLength > 18000) log.Text = log.Text[^12000..];
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {MediaTools.Redact(line)}\r\n");
    }
    private void TryAction(Action action) { try { action(); } catch (Exception e) { AddLog(e.Message); } }
    private static TextBox TextInput() => new() { Dock = DockStyle.Fill, BackColor = Background, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle };
    private static ComboBox Combo() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Background, ForeColor = Foreground, FlatStyle = FlatStyle.Flat };
    private static NumericUpDown Number(int min, int max, int value) => new() { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value, BackColor = Background, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle };
    private static CheckBox Check(string text, bool value) => new() { Text = text, Checked = value, Dock = DockStyle.Fill, ForeColor = Foreground };
    private static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = false, ForeColor = bold ? Foreground : Muted, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft };
    private static Button Button(string text, bool primary = false)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Height = 36, BackColor = primary ? Accent : Color.FromArgb(44, 53, 75), ForeColor = Foreground, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(3) };
        button.FlatAppearance.BorderSize = 0; return button;
    }
    private static void AddRow(TableLayoutPanel panel, string text, Control control)
    {
        var row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.Controls.Add(Label(text), 0, row); panel.Controls.Add(control, 1, row);
    }
    private static void AddFull(TableLayoutPanel panel, Control control, int height)
    {
        var row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.Controls.Add(control, 0, row); panel.SetColumnSpan(control, 2);
    }
}
