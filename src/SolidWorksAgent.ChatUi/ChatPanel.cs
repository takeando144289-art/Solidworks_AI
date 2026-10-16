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
    private readonly Dictionary<string, MessageBubble> _streamed = [];
    private readonly IChatNotifier? _notifier;
    private readonly ComboBox _choice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly TextBox _model = new() { PlaceholderText = "既定のモデル", Dock = DockStyle.Fill };
    private readonly ConversationView _conversation = new();
    private readonly ChatInputBox _input = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, PlaceholderText = "例：直径30 mm、長さ20 mmの円柱を作って" };
    private readonly Button _send = new() { Text = "送信", AutoSize = true };
    private readonly Button _cancel = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button _configure = new() { Text = "設定", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0) };
    private readonly Button _selectModel = new() { Text = "モデル選択", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0) };
    private readonly Button _newChat = new() { Text = "新しいチャット", Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label _status = new() { Text = "準備完了", Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(71, 85, 105) };
    private CancellationTokenSource? _request;
    private bool _busy, _importing;
    private readonly List<ChatAttachment> _attachments = [];
    private readonly FlowLayoutPanel _attachmentList = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Visible = false };
    private readonly RowStyle _attachmentRow = new(SizeType.Absolute, 0);
    private readonly Button _attach = new() { Text = "＋ 添付", AutoSize = true };

    // Preserve the constructor signature used by already installed COM add-ins.
    public ChatPanel(int solidWorksPid, Func<ChatSettings, int, IAgentSession>? sessionFactory = null)
        : this(solidWorksPid, sessionFactory, null) { }

    public ChatPanel(int solidWorksPid, Func<ChatSettings, int, IAgentSession>? sessionFactory, IChatNotifier? notifier)
    {
        _solidWorksPid = solidWorksPid;
        _sessionFactory = sessionFactory ?? AgentSession.Create;
        _testing = sessionFactory is not null;
        _notifier = notifier ?? (_testing ? null : new ChatNotifier(solidWorksPid > 0 ? solidWorksPid : Environment.ProcessId, () => { if (!IsDisposed) _input.Focus(); }));
        string? settingsError = null;
        try { _settings = ChatSettings.Load(); }
        catch (Exception ex) { _settings = new(); settingsError = ex.Message; }
        Dock = DockStyle.Fill;
        Font = new Font("Yu Gothic UI", 9F);
        BackColor = Color.White;
        MinimumSize = new Size(280, 280);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 36F, 34F, -1F, 24F, 80F, 38F })
            layout.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Insert(4, _attachmentRow);
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
        layout.Controls.Add(_attachmentList, 0, 4);
        layout.Controls.Add(_input, 0, 5);
        var sendRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        sendRow.Controls.AddRange([_send, _cancel, _attach]); layout.Controls.Add(sendRow, 0, 6); Controls.Add(layout);
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
            await ResetSessionsAsync(); _attachments.Clear(); RefreshAttachments(); _conversation.Clear(); Append("案内", "新しい会話を開始しました。");
        });
        _input.SubmitRequested += async (_, _) => await SendAsync();
        _attach.Click += async (_, _) =>
        {
            if (_busy || _importing) return;
            try
            {
                using var dialog = new OpenFileDialog { Title = "画像・図面・参考ファイルを添付", Multiselect = true, Filter = "すべてのファイル|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK) await AddAttachmentsAsync(dialog.FileNames);
            }
            catch (Exception ex) { Append("添付エラー", ex.Message); }
        };
        _conversation.MessageAdded += RegisterDropTarget;
        RegisterDropTarget(this);
        Append("案内", "選んだAIと直接やり取りできます。調査・操作・修正はAI自身が進めます。停止するときは「やめて」と入力してください。\nEnterで送信、Shift+Enterで改行。画像やファイルをドロップ、または「＋ 添付」で追加できます。");
        _cancel.Visible = false; _status.Text = "";
        if (settingsError is not null) Append("設定エラー", settingsError);
        if (solidWorksPid <= 0) Append("プレビュー", "SolidWorksに接続していません。アドインとして開くとCAD操作を利用できます。");
    }

    private void RegisterDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            try
            {
                e.Effect = !_busy && !_importing && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                    && (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            }
            catch (Exception) { e.Effect = DragDropEffects.None; }
        };
        control.DragDrop += async (_, e) =>
        {
            try { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddAttachmentsAsync(paths); }
            catch (Exception ex) { if (!IsDisposed) Append("添付エラー", ex.Message); }
        };
        foreach (Control child in control.Controls) RegisterDropTarget(child);
    }

    private async Task AddAttachmentsAsync(string[] paths)
    {
        if (_busy || _importing || IsDisposed) return;
        _importing = true; Busy(_busy); _status.Text = "添付ファイルを読み込んでいます…";
        try
        {
            var imported = await AttachmentStore.ImportAsync(_settings.ProjectDirectory, paths, _attachments);
            if (IsDisposed) return;
            _attachments.AddRange(imported); RefreshAttachments(); _input.Focus();
        }
        catch (Exception ex) { if (!IsDisposed) Append("添付エラー", ex.Message); }
        finally { _importing = false; Busy(_busy); if (!IsDisposed) _status.Text = ""; }
    }

    private void RefreshAttachments()
    {
        while (_attachmentList.Controls.Count > 0) _attachmentList.Controls[0].Dispose();
        foreach (var attachment in _attachments)
        {
            var chip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 5, 2) };
            var name = new Label { Text = attachment.Name, AutoEllipsis = true, Width = 150, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
            var remove = new Button { Text = "×", Width = 27, Height = 26, AccessibleName = attachment.Name + " を取り外す" };
            remove.Click += (_, _) => { if (!_busy && !_importing) { _attachments.Remove(attachment); RefreshAttachments(); } };
            chip.Controls.Add(name); chip.Controls.Add(remove); _attachmentList.Controls.Add(chip); RegisterDropTarget(chip);
        }
        _attachmentList.Visible = _attachments.Count > 0;
        _attachmentRow.Height = _attachments.Count > 0 ? 58 : 0;
    }

    private async Task RunSettingsActionAsync(Func<Task> action)
    {
        if (IsDisposed || Disposing || _busy || _importing) return;
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

    public object Status => new { Busy = _busy, PendingOperations = 0, Conversation = _conversation.TranscriptText };
    public bool BeginConnectionCheck()
    {
        // Exercise the same chat path as a typed request without replacing a draft.
        if (_busy || _importing || _attachments.Count > 0 || !string.IsNullOrWhiteSpace(_input.Text)) return false;
        _input.Text = "接続できているか確認して";
        _ = SendAsync();
        return true;
    }
    private void Busy(bool value)
    {
        if (IsDisposed) return;
        _busy = value;
        var editable = !value && !_importing;
        _choice.Enabled = _configure.Enabled = _model.Enabled = _selectModel.Enabled = _newChat.Enabled = _attach.Enabled = _attachmentList.Enabled = editable;
        _cancel.Visible = value; _cancel.Enabled = value; _send.Enabled = !_importing;
    }
    private void Append(string role, string text)
    {
        if (!IsDisposed) _conversation.AddMessage(role, text);
    }
    private void Notify(ChatNotification notification)
    {
        if (IsDisposed || Disposing) return;
        // A Windows notification failure must never escape into the native CAD host.
        try { _notifier?.Notify(notification, _settings.Provider.ToString()); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Chat notification: " + ex.Message); }
    }
    private void Display(AgentEvent item)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Display(item)); return; }
        if (item.Kind == "status") { _status.Text = item.Text; return; }
        if (item.Kind is not ("delta" or "message")) return;
        var id = item.ItemId ?? Guid.NewGuid().ToString();
        if (!_streamed.TryGetValue(id, out var bubble))
        {
            bubble = _conversation.AddMessage(_settings.Provider.ToString(), item.Text);
            _streamed[id] = bubble;
        }
        else _conversation.UpdateMessage(bubble, item.Kind == "delta" ? bubble.MessageText + item.Text : item.Text);
    }

    private async Task<string> AskAsync(AgentQuestion question, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            if (IsDisposed || token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
            _answer = completion;
            Append(_settings.Provider.ToString(), question.Text + (question.Options.Length > 0 ? "\n" + string.Join(" / ", question.Options) : ""));
            _status.Text = "AIからの確認に、このチャットで返信してください";
            Notify(ChatNotification.ReplyRequired);
        }
        if (InvokeRequired) BeginInvoke(Show); else Show();
        try { return await completion.Task.WaitAsync(token); }
        finally { if (ReferenceEquals(_answer, completion)) _answer = null; }
    }

    private async Task SendAsync()
    {
        if (_importing || (string.IsNullOrWhiteSpace(_input.Text) && _attachments.Count == 0)) return;
        var prompt = _input.Text; // User text is forwarded verbatim; the pane never converts it into a plan.
        if (_busy)
        {
            if (ChatIntents.Parse(prompt) == ChatIntent.Cancel)
            { _input.Clear(); Append("あなた", prompt); _request?.Cancel(); _status.Text = "AIに停止を要求しています…"; }
            else if (_answer is not null)
            { _input.Clear(); Append("あなた", prompt); _answer.TrySetResult(prompt.Trim()); _answer = null; _status.Text = "AIが応答を続けています…"; }
            else if (_activeSession is not null)
            {
                try { await _activeSession.SteerAsync(prompt, _request!.Token); _input.Clear(); Append("あなた", prompt); }
                catch (Exception ex) { _status.Text = ex.Message; }
            }
            return;
        }
        var attached = _attachments.ToArray();
        var message = new AgentMessage(string.IsNullOrWhiteSpace(prompt) ? "添付ファイルを確認してください。" : prompt, attached);
        if (attached.Any(a => !File.Exists(a.Path))) { Append("添付エラー", "添付ファイルが見つかりません。取り外して追加し直してください。"); return; }
        _input.Clear(); _attachments.Clear(); RefreshAttachments();
        Append("あなた", prompt + (attached.Length == 0 ? "" : "\n添付：" + string.Join("、", attached.Select(a => a.Name))));
        if (ChatIntents.Parse(prompt) == ChatIntent.Cancel) { _status.Text = "実行中の処理はありません"; return; }
        Busy(true); _request = new CancellationTokenSource(); _streamed.Clear();
        try
        {
            _settings.Providers[_settings.Provider].Model = _model.Text.Trim();
            if (!_testing) _settings.Save();
            if (!_sessions.TryGetValue(_settings.Provider, out var session))
            { session = _sessionFactory(_settings, _solidWorksPid); _sessions[_settings.Provider] = session; }
            _activeSession = session;
            _status.Text = _settings.Provider + " に送信しています…";
            await session.SendAsync(message, Display, AskAsync, _request.Token);
            _request.Token.ThrowIfCancellationRequested();
            if (!IsDisposed) { _status.Text = "応答が完了しました"; Notify(ChatNotification.Completed); }
        }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "停止しました"; }
        catch (Exception ex)
        {
            Append("接続エラー", ex.Message);
            if (!IsDisposed)
            {
                _status.Text = "AI接続を確認してください";
                Notify(ChatNotification.Failed);
                if (attached.Length > 0)
                {
                    _attachments.AddRange(attached); RefreshAttachments();
                    if (string.IsNullOrEmpty(_input.Text)) _input.Text = prompt;
                }
            }
        }
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
        if (disposing) { _request?.Cancel(); _ = DisposeSessionsAsync(); _model.Dispose(); _notifier?.Dispose(); }
        base.Dispose(disposing);
    }
}
