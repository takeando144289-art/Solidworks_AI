using System.Text.Json;
using SolidWorksAgent.Chat;

namespace SolidWorksAgent.ChatUi;

public sealed class SettingsDialog : Form
{
    public ChatSettings Settings { get; }
    private readonly Dictionary<string, TextBox> _fields = [];
    public SettingsDialog(ChatSettings current)
    {
        Settings = ChatSettings.FromJson(JsonSerializer.Serialize(current, ChatProtocol.Json));
        Text = "SolidWorks AI Chat — 接続設定"; Size = new(760, 560); MinimumSize = new(620, 500);
        StartPosition = FormStartPosition.CenterParent; Font = new("Yu Gothic UI", 9);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(16), AutoScroll = true };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 145)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 60));
        Controls.Add(grid);
        var note = new Label { Text = "Windows版CLIのログインを事前に完了してください。\nモデル欄を空にすると各CLIの既定モデルを使います。APIキーはここには保存しません。", AutoSize = true, Dock = DockStyle.Fill };
        grid.Controls.Add(note, 0, 0); grid.SetColumnSpan(note, 3); grid.RowStyles.Add(new(SizeType.Absolute, 60));
        Add("project", "作業フォルダー", Settings.ProjectDirectory, true);
        Add("cad", "swagent.exe", Settings.CadExecutable, false);
        Add("node", "Node.js（npm用）", Settings.NodeExecutable, false);
        foreach (var provider in ChatSettings.AvailableProviders) Add(provider.ToString(), provider + " CLI", Settings.Providers[provider].Executable, false);
        var help = new Label { Text = "CLIは .exe / .js / .mjs を選択できます。npmの.cmdも標準配置なら解決します。\nCLIが未導入の場合は docs/chat-addin.md の導入手順を参照してください。", AutoSize = true, Dock = DockStyle.Fill };
        grid.Controls.Add(help, 0, _fields.Count + 1); grid.SetColumnSpan(help, 3); grid.RowStyles.Add(new(SizeType.Absolute, 60));
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                Settings.ProjectDirectory = _fields["project"].Text.Trim(); Settings.CadExecutable = _fields["cad"].Text.Trim(); Settings.NodeExecutable = _fields["node"].Text.Trim();
                foreach (var provider in ChatSettings.AvailableProviders) Settings.Providers[provider].Executable = _fields[provider.ToString()].Text.Trim();
                Settings.ValidateCad(); Settings.Save(); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        grid.Controls.Add(save, 1, _fields.Count + 2); AcceptButton = save;
        void Add(string key, string label, string value, bool folder)
        {
            var row = _fields.Count + 1;
            grid.RowStyles.Add(new(SizeType.Absolute, 46));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var field = new TextBox { Text = value, Dock = DockStyle.Fill, Anchor = AnchorStyles.Left | AnchorStyles.Right }; _fields[key] = field;
            grid.Controls.Add(field, 1, row);
            var browse = new Button { Text = "参照", AutoSize = true };
            browse.Click += (_, _) =>
            {
                try
                {
                    if (folder) { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) field.Text = d.SelectedPath; }
                    else { using var d = new OpenFileDialog { Filter = "実行ファイル|*.exe;*.js;*.mjs;*.cmd|すべて|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) field.Text = d.FileName; }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "ファイル選択エラー", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            grid.Controls.Add(browse, 2, row);
        }
    }
}
