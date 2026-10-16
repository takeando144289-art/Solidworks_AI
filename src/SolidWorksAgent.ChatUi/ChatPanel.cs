using System.Drawing;
using SolidWorksAgent.Chat;

namespace SolidWorksAgent.ChatUi;

public sealed class ChatPanel : UserControl
{
    private readonly int _solidWorksPid;
    private ChatSettings _settings;
    private readonly Func<ChatSettings, int, IAgentSession> _sessionFactory;
    private readonly bool _testing;
    private readonly Dictionary<AiProvider, IAgentSession> _sessions = [];
    private IAgentSession? _activeSession;
    private TaskCompletionSource<string>? _answer;
    private readonly Dictionary<string, string> _streamed = [];
    private string? _streamItem;
    private readonly ComboBox _choice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _model = new() { PlaceholderText = "既定のモデル", Dock = DockStyle.Fill };
    private readonly RichTextBox _conversation = new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.None, DetectUrls = false };
    private readonly TextBox _input = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "例：直径30 mm、長さ20 mmの円柱を作って" };
    private readonly Button _send = new() { Text = "送信", AutoSize = true };
    private readonly Button _cancel = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button _configure = new() { Text = "設定", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0) };
    private readonly Button _selectModel = new() { Text = "モデル選択", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0) };
    private readonly Button _newChat = new() { Text = "新しいチャット", Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label _status = new() { Text = "準備完了", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(71, 85, 105) };
    private CancellationTokenSource? _request;
    private bool _busy;

    public ChatPanel(int solidWorksPid, Func<ChatSettings, int, IAgentSession>? sessionFactory = null)
    {
        _solidWorksPid = solidWorksPid;
        _sessionFactory = sessionFactory ?? AgentSession.Create;
        _testing = sessionFactory is not null;
        string? settingsError = null;
        try { _settings = ChatSettings.Load(); }
        catch (Exception ex) { _settings = new(); settingsError = ex.Message; }
        Dock = DockStyle.Fill;
        Font = new Font("Yu Gothic UI", 9F);
        BackColor = Color.White;
        MinimumSize = new Size(280, 280);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 36F, 34F, -1F, 24F, 80F, 38F })
            layout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(_choice, 0, 0); top.Controls.Add(new Label { Text = "SolidWorks", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 4) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        actions.Controls.Add(_configure, 0, 0); actions.Controls.Add(_selectModel, 1, 0); actions.Controls.Add(_newChat, 2, 0);
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(_conversation, 0, 2); layout.Controls.Add(_status, 0, 3);
        _input.BorderStyle = BorderStyle.FixedSingle;
        _input.PlaceholderText = "指示を入力 · Enterで送信、Shift+Enterで改行";
        layout.Controls.Add(_input, 0, 4);
        var sendRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        sendRow.Controls.AddRange([_send, _cancel]); layout.Controls.Add(sendRow, 0, 5); Controls.Add(layout);
        _choice.Items.AddRange(Enum.GetNames<AiProvider>());
        _choice.SelectedIndex = (int)_settings.Provider;
        _model.Text = _settings.Providers[_settings.Provider].Model;
        _choice.SelectedIndexChanged += (_, _) =>
        {
            _settings.Providers[_settings.Provider].Model = _model.Text;
            _settings.Provider = (AiProvider)_choice.SelectedIndex;
            _model.Text = _settings.Providers[_settings.Provider].Model;
        };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => { _request?.Cancel(); _status.Text = "停止要求済み。CAD操作中は保存・整理の完了を待ちます。"; };
        _configure.Click += async (_, _) => await RunSettingsActionAsync(async () =>
        {
            using var dialog = new SettingsDialog(_settings);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await ResetSessionsAsync();
            // Update the selection against the old settings before replacing them.
            _choice.SelectedIndex = (int)dialog.Settings.Provider;
            _settings = dialog.Settings;
            _model.Text = _settings.Providers[_settings.Provider].Model;
        });
        _selectModel.Click += async (_, _) => await RunSettingsActionAsync(() =>
        {
            using var dialog = new Form { Text = "モデル（空欄で既定）", Size = new Size(400, 160), StartPosition = FormStartPosition.CenterParent, Font = Font };
            var field = new TextBox { Text = _model.Text, Dock = DockStyle.Top };
            var save = new Button { Text = "保存", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
            dialog.Controls.Add(field); dialog.Controls.Add(save); dialog.AcceptButton = save;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var options = _settings.Providers[_settings.Provider];
                var previous = options.Model;
                options.Model = field.Text.Trim();
                try { if (!_testing) _settings.Save(); }
                catch { options.Model = previous; throw; }
                _model.Text = options.Model;
            }
            return Task.CompletedTask;
        });
        _newChat.Click += async (_, _) => await RunSettingsActionAsync(async () =>
        {
            await ResetSessionsAsync(); _conversation.Clear(); Append("案内", "新しい会話を開始しました。");
        });
        _input.KeyDown += async (_, e) => { if (!e.Shift && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); } };
        Append("案内", "選んだAIと直接やり取りできます。調査・操作・修正はAI自身が進めます。停止するときは「やめて」と入力してください。\nEnterで送信、Shift+Enterで改行。");
        _cancel.Visible = false; _status.Text = "";
        if (settingsError is not null) Append("設定エラー", settingsError);
        if (solidWorksPid <= 0) Append("プレビュー", "SolidWorksに接続していません。アドインとして開くとCAD操作を利用できます。");
    }

    private async Task RunSettingsActionAsync(Func<Task> action)
    {
        if (IsDisposed || Disposing || _busy) return;
        try { await action(); }
        catch (Exception ex)
        {
            // Exceptions must not escape async UI callbacks into the native CAD host.
            try
            {
                var folder = Path.GetDirectoryName(ChatSettings.SettingsPath)!;
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "addin.log"), $"{DateTimeOffset.Now:O} Chat settings: {ex}\n");
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (!IsDisposed && !Disposing) Append("設定エラー", ex.Message);
        }
    }

    public object Status => new { Busy = _busy, PendingOperations = 0, Conversation = _conversation.Text };
    public bool BeginConnectionCheck()
    {
        // Exercise the same chat path as a typed request without replacing a draft.
        if (_busy || !string.IsNullOrWhiteSpace(_input.Text)) return false;
        _input.Text = "接続できているか確認して";
        _ = SendAsync();
        return true;
    }
    private void Busy(bool value)
    {
        if (IsDisposed) return;
        _busy = value; _send.Enabled = !value; _choice.Enabled = !value; _configure.Enabled = !value;
        _model.Enabled = !value; _selectModel.Enabled = !value; _newChat.Enabled = !value;
        _cancel.Visible = value; _cancel.Enabled = value; _send.Enabled = true;
    }
    private void Append(string role, string text)
    {
        if (IsDisposed) return;
        _conversation.SelectionStart = _conversation.TextLength;
        _conversation.SelectionColor = role == "あなた" ? Color.FromArgb(37, 99, 235) : Color.FromArgb(15, 23, 42);
        _conversation.AppendText($"{role}\n{text}\n\n"); _conversation.ScrollToCaret();
    }
    private void Display(AgentEvent item)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Display(item)); return; }
        if (item.Kind == "status") { _status.Text = item.Text; return; }
        var id = item.ItemId ?? Guid.NewGuid().ToString();
        if (item.Kind == "delta")
        {
            if (_streamItem != id) { Append(_settings.Provider.ToString(), ""); _streamItem = id; }
            _conversation.AppendText(item.Text); _conversation.ScrollToCaret();
            _streamed[id] = _streamed.GetValueOrDefault(id, "") + item.Text;
        }
        else if (item.Kind == "message")
        {
            if (!_streamed.ContainsKey(id)) Append(_settings.Provider.ToString(), item.Text);
            else _conversation.AppendText("\n\n");
            _streamItem = null;
        }
    }

    private async Task<string> AskAsync(AgentQuestion question, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            if (IsDisposed) { completion.TrySetCanceled(); return; }
            _answer = completion;
            Append(_settings.Provider.ToString(), question.Text + (question.Options.Length > 0 ? "\n" + string.Join(" / ", question.Options) : ""));
            _status.Text = "AIからの確認に、このチャットで返信してください";
        }
        if (InvokeRequired) BeginInvoke(Show); else Show();
        try { return await completion.Task.WaitAsync(token); }
        finally { if (ReferenceEquals(_answer, completion)) _answer = null; }
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_input.Text)) return;
        var prompt = _input.Text; // User text is forwarded verbatim; the pane never converts it into a plan.
        if (_busy)
        {
            if (ChatIntents.Parse(prompt) == ChatIntent.Cancel)
            { _input.Clear(); Append("あなた", prompt); _request?.Cancel(); _status.Text = "AIに停止を要求しています…"; }
            else if (_answer is not null)
            { _input.Clear(); Append("あなた", prompt); _answer.TrySetResult(prompt.Trim()); }
            else if (_activeSession is not null)
            {
                try { await _activeSession.SteerAsync(prompt, _request!.Token); _input.Clear(); Append("あなた", prompt); }
                catch (Exception ex) { _status.Text = ex.Message; }
            }
            return;
        }
        _input.Clear(); Append("あなた", prompt);
        if (ChatIntents.Parse(prompt) == ChatIntent.Cancel) { _status.Text = "実行中の処理はありません"; return; }
        Busy(true); _request = new CancellationTokenSource(); _streamed.Clear(); _streamItem = null;
        try
        {
            _settings.Providers[_settings.Provider].Model = _model.Text.Trim();
            if (!_testing) _settings.Save();
            if (!_sessions.TryGetValue(_settings.Provider, out var session))
            { session = _sessionFactory(_settings, _solidWorksPid); _sessions[_settings.Provider] = session; }
            _activeSession = session;
            _status.Text = _settings.Provider + " に送信しています…";
            await session.SendAsync(prompt, Display, AskAsync, _request.Token);
            if (!IsDisposed) _status.Text = "";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "停止しました"; }
        catch (Exception ex) { Append("接続エラー", ex.Message); if (!IsDisposed) _status.Text = "AI接続を確認してください"; }
        finally { _answer = null; _activeSession = null; _request?.Dispose(); _request = null; Busy(false); }
    }

    private async Task ResetSessionsAsync()
    {
        foreach (var session in _sessions.Values) await session.DisposeAsync();
        _sessions.Clear(); _streamed.Clear();
    }

    private async Task DisposeSessionsAsync()
    {
        try { await ResetSessionsAsync(); }
        catch (Exception) { /* The panel is closing; do not throw into the native host. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _request?.Cancel(); _ = DisposeSessionsAsync(); _model.Dispose(); }
        base.Dispose(disposing);
    }
}
