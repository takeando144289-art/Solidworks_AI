using SolidWorksAgent.Chat;

namespace SolidWorksAgent.ChatUi;

public sealed class ChatHistoryDialog : Form
{
    private readonly CancellationTokenSource _closed = new();
    private bool _disposed;
    protected override void Dispose(bool disposing)
    { if (disposing && !_disposed) { _disposed = true; _closed.Cancel(); _closed.Dispose(); } base.Dispose(disposing); }
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "チャットを検索" };
    private IReadOnlyList<ChatRecord> _records;
    public string? SelectedChatId => (_list.SelectedItem as ChatRecord)?.Id;
    public ChatHistoryDialog(IReadOnlyList<ChatRecord> records, string currentId, Func<CancellationToken, Task<IReadOnlyList<ChatRecord>>>? import = null)
    {
        _records = records;
        Text = "チャット履歴"; StartPosition = FormStartPosition.CenterParent;
        Size = new(600, 440); MinimumSize = new(360, 280); ShowInTaskbar = false;
        Font = new("Yu Gothic UI", 9F);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 32)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 30)); layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.Controls.Add(_search, 0, 0); layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "選択して開くと履歴を表示します。指示を送ると会話を再開します。", AutoEllipsis = true }, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var open = new Button { Text = "開く", AutoSize = true };
        var cancel = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        var old = new Button { Text = "以前の履歴を取り込む", AutoSize = true, Visible = import is not null };

        old.Click += async (_, _) =>
        {
            old.Enabled = open.Enabled = false;
            try
            {
                _records = await import!(_closed.Token);
                if (!IsDisposed) { RefreshList(); old.Text = "以前の履歴を取り込む"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "履歴の取り込み"); }
            finally { if (!IsDisposed) { old.Enabled = true; open.Enabled = _list.Items.Count > 0; } }
        };
        buttons.Controls.AddRange([open, cancel, old]); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        void RefreshList()
        {
            _list.Items.Clear();
            _list.Items.AddRange(_records.Where(x => x.Title.Contains(_search.Text, StringComparison.CurrentCultureIgnoreCase)).Cast<object>().ToArray());
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            open.Enabled = _list.Items.Count > 0;
        }
        void Open() { if (SelectedChatId is not null) { DialogResult = DialogResult.OK; Close(); } }
        open.Click += (_, _) => Open(); _list.DoubleClick += (_, _) => Open(); _search.TextChanged += (_, _) => RefreshList();
        AcceptButton = open; CancelButton = cancel; RefreshList();
        var current = records.FirstOrDefault(x => x.Id == currentId);
        if (current is not null) _list.SelectedItem = current;
    }
}
