using System.Reflection;

namespace HoloFan;

sealed class MainForm : Form
{
    sealed class Item
    {
        public required string Path;
        public required string Name;            // no cartão, sem ".bin"
        public VideoInfo? Info;
        public bool Done;
        public readonly ListViewItem Row = new();
    }

    static readonly Color Red = Color.FromArgb(196, 43, 28);
    static readonly Color Green = Color.FromArgb(16, 124, 16);

    readonly ListView _list = new();
    readonly Button _add = new() { Text = "Adicionar vídeos...", AutoSize = true };
    readonly Button _remove = new() { Text = "Remover", AutoSize = true };
    readonly TextBox _folder = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly Button _browse = new() { Text = "Procurar...", AutoSize = true };
    readonly ComboBox _framing = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _colors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    readonly Label _status = new() { AutoSize = true, Text = "" };
    readonly Label _error = new() { AutoSize = true, ForeColor = Red, Text = "" };
    readonly Button _convert = new() { Text = "Converter", AutoSize = true };
    readonly NotifyIcon _tray = new();

    string? _ffmpeg;
    CancellationTokenSource? _cts;
    bool _closeWhenDone;

    public MainForm()
    {
        Text = "HoloFan — vídeos para o ventilador holográfico";
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 420);
        Size = new Size(820, 540);
        AllowDrop = true;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
            if (s != null) Icon = new Icon(s);
        _tray.Icon = Icon;
        _tray.Text = "HoloFan";
        _tray.Visible = true;
        _tray.BalloonTipClicked += (_, _) => Activate();
        _tray.MouseClick += (_, _) => Activate();

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.LabelEdit = true;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.AllowDrop = true;
        _list.Columns.Add("Nome no cartão", 130);
        _list.Columns.Add("Vídeo", 260);
        _list.Columns.Add("Duração", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Tamanho", 75, HorizontalAlignment.Right);
        _list.Columns.Add("Situação", 220);

        _framing.Items.AddRange(["Preencher o círculo (corta as laterais, como os originais)", "Vídeo inteiro (bordas pretas em volta)"]);
        _framing.SelectedIndex = 0;
        _colors.Items.AddRange(["Vivas (contraste puxado, como os originais)", "Naturais (as cores do vídeo)"]);
        _colors.SelectedIndex = 0;

        var hint = new Label
        {
            AutoSize = true,
            Text = "Arraste os vídeos para cá ou clique em Adicionar. Para trocar o nome no cartão, selecione o vídeo e " +
                   "aperte F2 (até 10 letras; letra acentuada conta 2).",
        };

        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        listButtons.Controls.AddRange([_add, _remove]);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        options.Controls.Add(Caption("Salvar em:"), 0, 0);
        options.Controls.Add(_folder, 1, 0);
        options.Controls.Add(_browse, 2, 0);
        options.Controls.Add(Caption("Enquadramento:"), 0, 1);
        options.Controls.Add(_framing, 1, 1);
        options.Controls.Add(Caption("Cores:"), 0, 2);
        options.Controls.Add(_colors, 1, 2);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_progress, 0, 0);
        bottom.Controls.Add(_convert, 1, 0);
        bottom.Controls.Add(_status, 0, 1);
        bottom.SetColumnSpan(_status, 2);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint);
        root.Controls.Add(_list);
        root.Controls.Add(listButtons);
        root.Controls.Add(options);
        root.Controls.Add(_error);
        root.Controls.Add(bottom);
        Controls.Add(root);
        root.AllowDrop = true;
        root.SizeChanged += (_, _) => hint.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal), 0);
        float scale = DeviceDpi / 96f;
        foreach (ColumnHeader col in _list.Columns) col.Width = (int)(col.Width * scale);

        _add.Click += (_, _) => PickVideos();
        _remove.Click += (_, _) => RemoveSelected();
        _browse.Click += (_, _) => PickFolder();
        _convert.Click += async (_, _) => await ConvertOrStop();
        _list.AfterLabelEdit += OnRename;
        _list.BeforeLabelEdit += (_, e) => { if (_cts != null) e.CancelEdit = true; };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelected();
            if (e.KeyCode == Keys.F2 && _cts == null && _list.SelectedItems.Count == 1) _list.SelectedItems[0].BeginEdit();
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        foreach (Control c in new Control[] { root, _list })
        {
            c.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _cts == null) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddVideos(files); };
        }

        _folder.Text = DefaultFolder();
        _ffmpeg = Ffmpeg.Find();
        if (_ffmpeg == null)
            _error.Text = "Falta o ffmpeg.exe: ele lê os vídeos e precisa ficar na mesma pasta do HoloFan.exe.";
        UpdateButtons();
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

    // ---------- lista ----------

    void PickVideos()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Escolha os vídeos",
            Multiselect = true,
            Filter = "Vídeos|*.mp4;*.m4v;*.mkv;*.webm;*.avi;*.mov;*.wmv;*.mpg;*.mpeg;*.flv;*.3gp;*.ts;*.gif|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddVideos(dlg.FileNames);
    }

    void AddVideos(IEnumerable<string> paths)
    {
        if (_cts != null) return;
        _error.Text = _ffmpeg == null ? _error.Text : "";
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            if (Items().Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new Item { Path = path, Name = UniqueName(HoloFormat.CardName(path), null) };
            item.Row.Tag = item;
            item.Row.Text = item.Name;
            item.Row.SubItems.Add(Path.GetFileName(path));
            item.Row.SubItems.Add("");
            item.Row.SubItems.Add("");
            item.Row.SubItems.Add("");
            item.Row.UseItemStyleForSubItems = false;
            _list.Items.Add(item.Row);
            if (_ffmpeg == null) SetState(item, "falta o ffmpeg", Red);
            else _ = ProbeAsync(item);
        }
        UpdateButtons();
    }

    async Task ProbeAsync(Item item)
    {
        SetState(item, "lendo...", SystemColors.GrayText);
        try
        {
            item.Info = await Task.Run(() => Ffmpeg.ProbeAsync(_ffmpeg!, item.Path));
        }
        catch (Exception ex)
        {
            item.Info = new VideoInfo(null, false, false, ex.Message);
        }
        if (item.Row.ListView == null) return; // removido enquanto lia
        if (item.Info.Error != null)
            SetState(item, item.Info.Error, Red);
        else
        {
            item.Row.SubItems[2].Text = item.Info.Duration is { } d ? FormatDuration(d) : "?";
            item.Row.SubItems[3].Text = EstimatedSize(item) is { } size ? $"{size / 1e6:0} MB" : "?";
            SetState(item, item.Info.HasAudio ? "pronto para converter" : "pronto para converter (sem som)", SystemColors.WindowText);
        }
        UpdateButtons();
    }

    static long? EstimatedSize(Item item) =>
        item.Info?.Duration is { } d ? HoloFormat.FileSize((int)Math.Ceiling(d.TotalSeconds * HoloFormat.Fps)) : null;

    static string FormatDuration(TimeSpan d) => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    void RemoveSelected()
    {
        if (_cts != null) return;
        foreach (ListViewItem row in _list.SelectedItems.Cast<ListViewItem>().ToList()) _list.Items.Remove(row);
        UpdateButtons();
    }

    IEnumerable<Item> Items() => _list.Items.Cast<ListViewItem>().Select(r => (Item)r.Tag!);

    void SetState(Item item, string text, Color color)
    {
        var cell = item.Row.SubItems[4];
        cell.Text = text;
        cell.ForeColor = color;
    }

    void OnRename(object? sender, LabelEditEventArgs e)
    {
        if (e.Label == null) return; // edição cancelada
        var item = (Item)_list.Items[e.Item].Tag!;
        string name = e.Label.Trim();
        if (name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        string? problem =
            name.Length == 0 ? "O nome não pode ficar vazio." :
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "O nome não pode ter \\ / : * ? \" < > |" :
            HoloFormat.NameBytes(name) > HoloFormat.MaxNameBytes ? $"\"{name}\" é comprido demais: até 10 letras (letra acentuada conta 2)." :
            name.EndsWith('.') ? "O nome não pode terminar em ponto." :
            Items().Any(i => i != item && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"Já tem outro vídeo com o nome \"{name}\" na lista." :
            null;
        e.CancelEdit = true;
        if (problem != null)
        {
            _error.Text = problem;
            return;
        }
        _error.Text = _ffmpeg == null ? _error.Text : "";
        item.Name = name;
        BeginInvoke(() => item.Row.Text = name);
    }

    /// <summary>Um nome que não existe na pasta de destino nem em outro item da lista ("Gabby", "Gabby 2"...).</summary>
    string UniqueName(string name, Item? self)
    {
        bool Taken(string n) =>
            Items().Any(i => i != self && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)) ||
            File.Exists(Path.Combine(_folder.Text, n + ".bin"));
        if (!Taken(name)) return name;
        for (int k = 2; ; k++)
        {
            string suffix = " " + k;
            string candidate = HoloFormat.FitName(TrimToBytes(name, HoloFormat.MaxNameBytes - suffix.Length)) + suffix;
            if (!Taken(candidate)) return candidate;
        }
    }

    static string TrimToBytes(string s, int max)
    {
        while (HoloFormat.NameBytes(s) > max) s = s[..^1];
        return s;
    }

    // ---------- pasta ----------

    /// <summary>O cartão de memória: o primeiro removível que já tem .bin, senão o primeiro removível.</summary>
    static string DefaultFolder()
    {
        try
        {
            var removable = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady).ToList();
            var withBin = removable.FirstOrDefault(d =>
            {
                try { return Directory.EnumerateFiles(d.RootDirectory.FullName, "*.bin").Any(); }
                catch { return false; }
            });
            var drive = withBin ?? removable.FirstOrDefault();
            if (drive != null) return drive.RootDirectory.FullName;
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Onde salvar os .bin (a raiz do cartão de memória)",
            UseDescriptionForTitle = true,
            SelectedPath = _folder.Text,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    // ---------- conversão ----------

    void UpdateButtons()
    {
        bool busy = _cts != null;
        _add.Enabled = !busy;
        _remove.Enabled = !busy && _list.SelectedItems.Count > 0;
        _browse.Enabled = !busy;
        _framing.Enabled = !busy;
        _colors.Enabled = !busy;
        _convert.Text = busy ? "Parar" : "Converter";
        _convert.Enabled = busy || (_ffmpeg != null && Pending().Any());
    }

    /// <summary>Os que ainda não foram convertidos; um que deu erro na conversão entra de novo.</summary>
    IEnumerable<Item> Pending() => Items().Where(i => !i.Done && i.Info is { Error: null });

    async Task ConvertOrStop()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _convert.Enabled = false;
            return;
        }
        var todo = Pending().ToList();
        if (todo.Count == 0) return;

        string folder = _folder.Text;
        if (!Directory.Exists(folder))
        {
            _error.Text = $"A pasta {folder} não existe (o cartão está no PC?). Escolha outra em Procurar.";
            return;
        }
        long need = todo.Sum(i => EstimatedSize(i) ?? 0);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            if (drive.IsReady && need > drive.AvailableFreeSpace)
            {
                _error.Text = $"Não cabe: os vídeos vão ocupar {need / 1e6:0} MB e só tem {drive.AvailableFreeSpace / 1e6:0} MB livres em {drive.Name}.";
                return;
            }
            if (drive.IsReady && drive.DriveFormat == "FAT32" && todo.Any(i => EstimatedSize(i) > uint.MaxValue))
            {
                _error.Text = "Algum vídeo passa de 4 GB (mais de 2 horas), que é o limite de um arquivo no cartão. Corte o vídeo antes.";
                return;
            }
        }
        catch { /* sem informação do disco: tenta assim mesmo */ }
        _error.Text = "";

        var options = new ConvertOptions(_framing.SelectedIndex == 1 ? Framing.Whole : Framing.Fill, _colors.SelectedIndex == 0);
        _cts = new CancellationTokenSource();
        UpdateButtons();
        _list.LabelEdit = false;

        int ok = 0;
        var failures = new List<string>();
        bool stopped = false;
        for (int n = 0; n < todo.Count; n++)
        {
            var item = todo[n];
            if (_cts.IsCancellationRequested) { stopped = true; break; }
            item.Name = UniqueName(item.Name, item);
            item.Row.Text = item.Name;
            string output = Path.Combine(folder, item.Name + ".bin");
            _status.Text = $"Convertendo {n + 1} de {todo.Count}: {Path.GetFileName(item.Path)}";
            SetState(item, "convertendo...", SystemColors.Highlight);
            int index = n;
            var progress = new Progress<double>(f =>
            {
                if (item.Done || _cts == null) return;
                SetState(item, $"convertendo... {f * 100:0}%", SystemColors.Highlight);
                _progress.Value = (int)Math.Clamp((index + f) / todo.Count * 1000, 0, 1000);
            });
            try
            {
                var ct = _cts.Token;
                await Task.Run(() => VideoConverter.ConvertAsync(_ffmpeg!, item.Path, output, options, item.Info!, progress, ct));
                item.Done = true;
                ok++;
                SetState(item, $"pronto: {item.Name}.bin", Green);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                SetState(item, "parado", SystemColors.GrayText);
                break;
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // O ffmpeg morto pelo Parar também chega aqui, como erro de leitura.
                stopped = true;
                SetState(item, "parado", SystemColors.GrayText);
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
                SetState(item, "erro: " + ex.Message, Red);
            }
        }

        _cts.Dispose();
        _cts = null;
        _list.LabelEdit = true;
        _progress.Value = stopped ? 0 : 1000;
        _status.Text = stopped ? "Parado." : $"Pronto: {ok} de {todo.Count} em {folder}";
        UpdateButtons();

        string title = stopped ? "Conversão parada" : failures.Count == 0 ? "Vídeos prontos" : "Conversão terminou com erro";
        string body = $"{ok} {(ok == 1 ? "vídeo convertido" : "vídeos convertidos")} em {folder}";
        if (failures.Count > 0) body += "\n" + string.Join("\n", failures);
        _tray.ShowBalloonTip(5000, title, body, failures.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);

        if (_closeWhenDone) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts != null)
        {
            // Para a conversão (o .tmp é apagado) e fecha quando ela terminar de parar.
            _closeWhenDone = true;
            _cts.Cancel();
            e.Cancel = true;
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
