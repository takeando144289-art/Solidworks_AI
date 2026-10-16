using SolidWorksAgent.Chat;

namespace SolidWorksAgent.ChatUi;

public sealed class ModelSelectionDialog : Form
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentModel>>> _load;
    private readonly CancellationTokenSource _closed = new();
    private readonly ListBox _models = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "モデル一覧" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly CheckBox _manual = new() { Text = "モデル名を直接指定する", Dock = DockStyle.Fill };
    private readonly TextBox _custom = new() { Dock = DockStyle.Fill, Enabled = false, AccessibleName = "モデル名の直接指定" };
    private readonly Button _refresh = new() { Text = "一覧を再取得", AutoSize = true };
    private readonly Button _save = new() { Text = "選択して保存", AutoSize = true };
    private bool _loading, _disposeRequested;
    public string SelectedModel => _manual.Checked ? _custom.Text.Trim() : (_models.SelectedItem as AgentModel)?.Id ?? "";

    public ModelSelectionDialog(AiProvider provider, string current, Func<CancellationToken, Task<IReadOnlyList<AgentModel>>> load)
    {
        _load = load;
        Text = provider + "のモデル選択"; Font = new Font("Yu Gothic UI", 9F);
        StartPosition = FormStartPosition.CenterParent; Size = new Size(520, 500); MinimumSize = new Size(420, 400);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 48F, -1F, 60F, 28F, 30F, 10F, 38F })
            layout.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_models, 0, 1); layout.Controls.Add(_detail, 0, 2);
        layout.Controls.Add(_manual, 0, 3); layout.Controls.Add(_custom, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([_save, cancel, _refresh]); layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout); AcceptButton = _save; CancelButton = cancel;
        _custom.Text = current;
        Populate([], current);
        _models.SelectedIndexChanged += (_, _) => ShowDetail();
        _manual.CheckedChanged += (_, _) => { _custom.Enabled = _manual.Checked; _models.Enabled = !_manual.Checked; };
        _save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _refresh.Click += async (_, _) => await LoadModelsAsync();
        Shown += async (_, _) => await LoadModelsAsync();
        FormClosed += (_, _) => { if (!_disposeRequested) _closed.Cancel(); };
        ShowDetail();
    }
    private void Populate(IReadOnlyList<AgentModel> models, string selected)
    {
        _models.BeginUpdate();
        try
        {
            _models.Items.Clear();
            _models.Items.Add(new AgentModel("", "既定のモデル（AI側の設定に従う）", "モデルを固定せず、AI側で設定されている既定モデルを使います。"));
            foreach (var model in models) _models.Items.Add(model);
            if (selected.Length > 0 && !models.Any(x => x.Id == selected))
                _models.Items.Add(new AgentModel(selected, "現在の設定：" + selected, "現在設定されているモデルです。取得した一覧には含まれていません。"));
            _models.SelectedItem = _models.Items.Cast<AgentModel>().First(x => x.Id == selected);
        }
        finally { _models.EndUpdate(); }
    }
    private void ShowDetail()
    {
        if (_models.SelectedItem is AgentModel model)
            _detail.Text = model.Id.Length == 0 ? model.Description : model.Id + "\n" + model.Description;
    }
    private async Task LoadModelsAsync()
    {
        if (_loading || IsDisposed) return;
        _loading = true; _refresh.Enabled = false; _status.Text = "モデル一覧を取得しています…";
        try
        {
            var models = await _load(_closed.Token);
            if (IsDisposed || _closed.IsCancellationRequested) return;
            Populate(models, SelectedModel); ShowDetail();
            _status.Text = models.Count > 0 ? "使用するモデルを選んでください。次の送信から反映されます。" : "モデルが見つかりませんでした。ログインを確認して再取得してください。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !_closed.IsCancellationRequested) _status.Text = "一覧を取得できませんでした。" + ex.Message;
        }
        finally { _loading = false; if (_disposeRequested) _closed.Dispose(); else if (!IsDisposed) _refresh.Enabled = true; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposeRequested)
        {
            _disposeRequested = true; _closed.Cancel();
            if (!_loading) _closed.Dispose();
        }
        // A pending lookup disposes its cancellation source after its continuation finishes.
        base.Dispose(disposing);
    }
}
