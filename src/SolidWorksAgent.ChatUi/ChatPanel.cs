using System.Drawing;
using System.Text.Json;
using SolidWorksAgent.Chat;

namespace SolidWorksAgent.ChatUi;

public sealed class ChatPanel : UserControl
{
    private readonly int _solidWorksPid;
    private ChatSettings _settings;
    private readonly IChatProvider _provider;
    private readonly Func<CadCommand, bool, Task<string>>? _execute;
    private readonly List<ChatMessage> _history = [];
    private readonly ComboBox _choice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _model = new() { PlaceholderText = "既定のモデル", Dock = DockStyle.Fill };
    private readonly RichTextBox _conversation = new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.None, DetectUrls = false };
    private readonly TextBox _input = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "例：直径30 mm、長さ20 mmの円柱を作って" };
    private readonly Button _send = new() { Text = "送信", AutoSize = true };
    private readonly Button _cancel = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button _configure = new() { Text = "⋯", Width = 36 };
    private readonly Label _status = new() { Text = "準備完了", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(71, 85, 105) };
    private CancellationTokenSource? _request;
    private CadCommand[] _pending = [];
    private string _plannedDocument = "";
    private bool _busy;
    private bool _stopAfterCurrent;

    public ChatPanel(int solidWorksPid, IChatProvider? provider = null, Func<CadCommand, bool, Task<string>>? execute = null)
    {
        _solidWorksPid = solidWorksPid;
        _provider = provider ?? new CliAiProvider();
        _execute = execute;
        string? settingsError = null;
        try { _settings = ChatSettings.Load(); }
        catch (Exception ex) { _settings = new(); settingsError = ex.Message; }
        Dock = DockStyle.Fill;
        Font = new Font("Yu Gothic UI", 9F);
        BackColor = Color.White;
        MinimumSize = new Size(280, 280);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 36F, -1F, 24F, 80F, 38F })
            layout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        top.Controls.Add(_choice, 0, 0); top.Controls.Add(new Label { Text = "SolidWorks", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0); top.Controls.Add(_configure, 2, 0);
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(_conversation, 0, 1); layout.Controls.Add(_status, 0, 2);
        _input.BorderStyle = BorderStyle.FixedSingle;
        _input.PlaceholderText = "指示を入力 · Enterで送信、Shift+Enterで改行";
        layout.Controls.Add(_input, 0, 3);
        var sendRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        sendRow.Controls.AddRange([_send, _cancel]); layout.Controls.Add(sendRow, 0, 4); Controls.Add(layout);
        _choice.Items.AddRange(Enum.GetNames<AiProvider>());
        _choice.SelectedIndex = (int)_settings.Provider;
        _model.Text = _settings.Providers[_settings.Provider].Model;
        _choice.SelectedIndexChanged += (_, _) =>
        {
            _settings.Providers[_settings.Provider].Model = _model.Text;
            _settings.Provider = (AiProvider)_choice.SelectedIndex;
            _model.Text = _settings.Providers[_settings.Provider].Model;
            ClearPlan();
        };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => { _stopAfterCurrent = true; _request?.Cancel(); _status.Text = "停止要求済み。CAD操作中は保存・整理の完了を待ちます。"; };
        _configure.Click += (_, _) =>
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("接続設定…", null, (_, _) =>
            {
                using var dialog = new SettingsDialog(_settings);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { _settings = dialog.Settings; _choice.SelectedIndex = (int)_settings.Provider; _model.Text = _settings.Providers[_settings.Provider].Model; ClearPlan(); }
            });
            menu.Items.Add("モデル…", null, (_, _) =>
            {
                using var dialog = new Form { Text = "モデル（空欄で既定）", Size = new Size(400, 160), StartPosition = FormStartPosition.CenterParent, Font = Font };
                var field = new TextBox { Text = _model.Text, Dock = DockStyle.Top };
                var save = new Button { Text = "保存", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                dialog.Controls.Add(field); dialog.Controls.Add(save); dialog.AcceptButton = save;
                if (dialog.ShowDialog(this) == DialogResult.OK) _model.Text = field.Text.Trim();
            });
            menu.Items.Add("新しい会話", null, (_, _) => { _history.Clear(); _conversation.Clear(); ClearPlan(); Append("案内", "新しい会話を開始しました。"); });
            menu.Closed += (_, _) => menu.Dispose(); menu.Show(_configure, new Point(0, _configure.Height));
        };
        _input.KeyDown += async (_, e) => { if (!e.Shift && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); } };
        Append("案内", "日本語で指示してください。接続や形状の確認はそのまま結果を返します。変更内容を提案したら「実行して」、取り消すときは「やめて」と返信してください。\nEnterで送信、Shift+Enterで改行。");
        _cancel.Visible = false; _status.Text = "";
        if (settingsError is not null) Append("設定エラー", settingsError);
        if (solidWorksPid <= 0) Append("プレビュー", "SolidWorksに接続していません。アドインとして開くとCAD操作を利用できます。");
    }

    private void ClearPlan() { _pending = []; _plannedDocument = ""; }
    public object Status => new { Busy = _busy, PendingOperations = _pending.Length, Conversation = _conversation.Text };
    public bool BeginConnectionCheck()
    {
        // Exercise the same chat path as a typed request without replacing a draft or proposal.
        if (_busy || _pending.Length > 0 || !string.IsNullOrWhiteSpace(_input.Text)) return false;
        _input.Text = "接続できているか確認して";
        _ = SendAsync();
        return true;
    }
    private void Busy(bool value)
    {
        if (IsDisposed) return;
        _busy = value; _send.Enabled = !value; _choice.Enabled = !value; _configure.Enabled = !value;
        _model.Enabled = !value;
        _cancel.Visible = value; _cancel.Enabled = value; _send.Enabled = true;
    }
    private void Append(string role, string text)
    {
        if (IsDisposed) return;
        _conversation.SelectionStart = _conversation.TextLength;
        _conversation.SelectionColor = role == "あなた" ? Color.FromArgb(37, 99, 235) : Color.FromArgb(15, 23, 42);
        _conversation.AppendText($"{role}\n{text}\n\n"); _conversation.ScrollToCaret();
    }
    private async Task<string> ContextAsync() => _solidWorksPid <= 0 ? "Preview: no SolidWorks connected."
        : await ExecuteCadAsync(new("info", []), false);

    private Task<string> ExecuteCadAsync(CadCommand command, bool overwrite) => _execute is not null
        ? _execute(command, overwrite) : new CadCliClient(_settings, _solidWorksPid).ExecuteAsync(command, overwrite);

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_input.Text)) return;
        var prompt = _input.Text.Trim(); var intent = ChatIntents.Parse(prompt);
        if (_busy)
        {
            if (intent == ChatIntent.Cancel) { _input.Clear(); Append("あなた", prompt); _stopAfterCurrent = true; _request?.Cancel(); _status.Text = "停止要求済み。実行中のCAD操作の完了を待ちます。"; }
            else _status.Text = "応答待ちです。「やめて」で停止できます。";
            return;
        }
        if (prompt.Length > 16000) { Append("入力", "指示は16,000文字以内にしてください。"); return; }
        _input.Clear(); Append("あなた", prompt); _history.Add(new("user", prompt));
        if (intent == ChatIntent.Cancel) { ClearPlan(); Append("案内", "操作案を取り消しました。"); _history.Add(new("host", "User cancelled pending operations.")); _status.Text = ""; return; }
        if (intent is ChatIntent.Execute or ChatIntent.ExecuteWithOverwrite)
        {
            if (_pending.Length == 0) { Append("案内", "実行待ちの操作はありません。作りたいものや変更内容を教えてください。"); return; }
            await RunAsync(intent == ChatIntent.ExecuteWithOverwrite); return;
        }
        ClearPlan(); Busy(true); _stopAfterCurrent = false;
        _request = new CancellationTokenSource();
        try
        {
            _settings.Providers[_settings.Provider].Model = _model.Text.Trim();
            if (_execute is null) _settings.Save();
            _status.Text = "現在のドキュメントを確認中…";
            var context = await ContextAsync();
            _request.Token.ThrowIfCancellationRequested();
            _status.Text = $"{_settings.Provider} が考えています…";
            var reply = await _provider.ReplyAsync(_settings, _history, context, _request.Token);
            _request.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            foreach (var command in reply.Commands) CommandCatalog.Validate(command);
            _history.Add(new("assistant", JsonSerializer.Serialize(reply, ChatProtocol.Json)));
            Append(_settings.Provider.ToString(), reply.Reply);
            _pending = reply.Commands; _plannedDocument = context;
            if (_pending.Length == 0) { _status.Text = ""; return; }
            if (_pending.All(ChatIntents.IsReadOnly))
            {
                Busy(false);
                await RunAsync(false);
                return;
            }
            ShowPlan();
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "AIへの問い合わせを停止しました"; }
        catch (Exception ex) { Append("エラー", ex.Message); if (!IsDisposed) _status.Text = "設定や指示を確認してください"; }
        finally { _request?.Dispose(); _request = null; Busy(false); }
    }

    private void ShowPlan()
    {
            var details = string.Join("\n", _pending.Select((c, i) =>
                $"{i + 1}. {OperationName(c.Command)}" + (c.Arguments.Length == 0 ? "" : "：" + string.Join("、", c.Arguments.Where(a => a.Name != "expected").Select(a => $"{ArgumentName(a.Name)}={a.Value}")))));
            var notice = "予定している操作\n" + details + "\n\nよければ「実行して」と返信してください。変更や取り消しもここに入力できます。既存ファイルの上書きが必要な場合は「上書きを許可して実行」と明示してください。";
            Append("案内", notice); _history.Add(new("host", notice));
            _status.Text = "返信をお待ちしています";
    }

    private async Task RunAsync(bool overwrite)
    {
        if (_busy || _pending.Length == 0) return;
        var commands = _pending;
        // Consume once: repeated approval must never replay a completed or failed batch.
        _pending = []; Busy(true); _stopAfterCurrent = false;
        var ownsRequest = _request is null;
        if (ownsRequest) _request = new CancellationTokenSource();
        var results = new List<string>();
        try
        {
            foreach (var command in commands) CommandCatalog.Validate(command);
            if (_solidWorksPid <= 0) throw new InvalidOperationException("SolidWorksアドイン内で実行してください。");
            var current = await ContextAsync();
            if (DocumentKey(current) != DocumentKey(_plannedDocument))
                throw new InvalidOperationException("操作案の作成後にアクティブなドキュメントが変わりました。もう一度指示を送信してください。");
            if (_stopAfterCurrent || IsDisposed) return;
            for (var i = 0; i < commands.Length; i++)
            {
                if (_stopAfterCurrent || IsDisposed) { Append("実行", "残りの操作を停止しました。"); break; }
                _status.Text = $"{OperationName(commands[i].Command)}…（{i + 1}/{commands.Length}）";
                var result = await ExecuteCadAsync(commands[i], overwrite);
                _history.Add(new("host", $"Executed {commands[i].Command}: {result}"));
                results.Add($"Executed {commands[i].Command}: {result}");
                if (commands[i].Command == "info") Append("確認結果", ConnectionSummary(result));
            }
            if (results.Count > 0 && !commands.All(c => c.Command == "info") && !IsDisposed)
            {
                if (_stopAfterCurrent) Append("実行結果", string.Join("\n", results));
                else
                {
                    _status.Text = "結果をまとめています…";
                    try
                    {
                        var summary = await _provider.ReplyAsync(_settings, _history,
                            (commands.All(ChatIntents.IsReadOnly)
                                ? "Read-only inspection completed. Answer the original request; if the user requested a change, propose it using these verified results for chat approval. Do not execute it.\n"
                                : "Execution completed. Summarize ONLY these verified results. Return commands: [].\n") + string.Join("\n", results), _request!.Token);
                        _request!.Token.ThrowIfCancellationRequested();
                        // Only an inspection can produce a follow-up proposal, never automatic changes.
                        Append(_settings.Provider.ToString(), summary.Reply); _history.Add(new("assistant", JsonSerializer.Serialize(summary, ChatProtocol.Json)));
                        if (commands.All(ChatIntents.IsReadOnly) && summary.Commands.Length > 0)
                        {
                            foreach (var command in summary.Commands) CommandCatalog.Validate(command);
                            _plannedDocument = await ContextAsync();
                            if (DocumentKey(_plannedDocument) != DocumentKey(current)) throw new InvalidOperationException("確認中にドキュメントが変わりました。再度指示してください。");
                            _pending = summary.Commands; ShowPlan();
                        }
                    }
                    catch (Exception ex) { Append("実行結果", string.Join("\n", results)); Append("案内", "操作は完了しました。要約を取得できませんでした：" + ex.Message); }
                }
            }
            if (!IsDisposed) _status.Text = _stopAfterCurrent ? "停止しました" : _pending.Length > 0 ? "返信をお待ちしています" : "";
        }
        catch (Exception ex)
        {
            if (results.Count > 0) Append("完了した操作", string.Join("\n", results));
            _history.Add(new("host", "Execution stopped with error. Do not assume success: " + ex.Message));
            Append("実行エラー", ex.Message); if (!IsDisposed) _status.Text = "実行を停止しました";
        }
        finally { if (ownsRequest) { _request?.Dispose(); _request = null; } if (!IsDisposed) { if (_pending.Length == 0) ClearPlan(); Busy(false); } }
    }
    private static string ConnectionSummary(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var title = root.TryGetProperty("Title", out var t) ? t.GetString() : null;
        var connected = root.TryGetProperty("Connected", out var c) && c.ValueKind == JsonValueKind.True;
        return connected ? "SOLIDWORKSに接続できています。" + (string.IsNullOrEmpty(title) ? "現在開いているドキュメントはありません。" : $"現在のドキュメントは「{title}」です。") : "SOLIDWORKSの接続を確認できませんでした。";
    }
    private static string OperationName(string command) => command switch
    {
        "sketch list" or "sketch inspect" => "スケッチの寸法・拘束を確認", "sketch fully-define" => "スケッチを完全定義",
        "sketch add-dimension" => "駆動寸法を追加", "sketch add-relation" => "幾何拘束を追加",
        "info" => "接続の確認", "documents list" => "開いているドキュメントの確認", "part inspect" => "部品形状の確認",
        "part create-cylinder" => "円柱を作成", "part create-shaft" => "段付きシャフトを作成", "part create-tube" => "パイプを作成",
        "assembly create-bearing-test" => "軸受アセンブリを作成", "assembly analyze" => "アセンブリの干渉を確認",
        "drawing create-shaft" => "シャフトの図面を作成", "drawing inspect" => "図面の確認", "view isometric" => "等角表示に変更", _ => command
    };
    private static string ArgumentName(string name) => name switch
    {
        "sketch" => "スケッチ", "kind" => "種類", "entities" => "対象要素", "value" => "寸法(mm)", "diameter" => "直径(mm)", "length" => "長さ(mm)", "diameters" => "各段の直径(mm)", "lengths" => "各段の長さ(mm)",
        "outer-diameter" => "外径(mm)", "inner-diameter" => "内径(mm)", "output" => "保存先", "model" => "モデル", "drawing" => "図面", _ => name
    };
    private static string DocumentKey(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return string.Join("|", new[] { "Title", "Path", "Type" }.Select(k => doc.RootElement.TryGetProperty(k, out var p) ? p.ToString() : ""));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _stopAfterCurrent = true; _request?.Cancel(); _model.Dispose(); }
        base.Dispose(disposing);
    }
}
