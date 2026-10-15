using System.Reflection;
using SolidWorksAgent.Chat;
using SolidWorksAgent.ChatUi;

internal static class Program
{
    [STAThread]
    static int Main()
    {
        ApplicationConfiguration.Initialize();
        int failures = 0, count = 0;
        using var window = new Form { Size = new(420, 780) };
        window.Shown += async (_, _) =>
        {
            async Task Test(string name, Func<Task> action)
            {
                count++;
                try { await action(); Console.WriteLine("PASS " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
            }
            await Test("Read-only confirmation executes and reports in chat", async () =>
            {
                using var f = new Fixture(window, new ChatReply("確認します。", [new("info", [])]));
                await f.Send("接続を確認して");
                Check(f.Text.Contains("SOLIDWORKSに接続できています。") && f.Writes.Count == 0);
            });
            await Test("Changes await text approval and cannot replay", async () =>
            {
                using var f = new Fixture(window, Proposal());
                await f.Send("円柱を作って"); Check(f.Writes.Count == 0 && f.Text.Contains("実行して"));
                await f.Send("実行して"); Check(f.Writes.Count == 1 && !f.Writes[0]);
                await f.Send("実行して"); Check(f.Writes.Count == 1 && f.Text.Contains("実行待ちの操作はありません"));
                Check(!f.Panel.Controls.Cast<Control>().SelectMany(Descendants).Any(c => c.Text == "この操作を実行"));
            });
            await Test("Cancellation removes the pending batch", async () =>
            {
                using var f = new Fixture(window, Proposal());
                await f.Send("円柱を作って"); await f.Send("やめて"); await f.Send("実行して"); Check(f.Writes.Count == 0);
            });
            await Test("Explicit overwrite is scoped to one batch", async () =>
            {
                using var f = new Fixture(window, Proposal(), new("完了", []), Proposal());
                await f.Send("円柱を作って"); await f.Send("上書きを許可して実行");
                await f.Send("別の円柱を作って"); await f.Send("実行して");
                Check(f.Writes.SequenceEqual(new[] { true, false }));
            });
            await Test("Revised request invalidates previous batch", async () =>
            {
                using var f = new Fixture(window, Proposal(), new("寸法を教えてください。", []));
                await f.Send("円柱を作って"); await f.Send("長さを変更したい"); await f.Send("実行して"); Check(f.Writes.Count == 0);
            });
            await Test("Changed active document prevents execution", async () =>
            {
                using var f = new Fixture(window, Proposal());
                await f.Send("円柱を作って"); f.Title = "Other part"; await f.Send("実行して");
                Check(f.Writes.Count == 0 && f.Text.Contains("ドキュメントが変わりました"));
            });
            await Test("Failure consumes approval", async () =>
            {
                using var f = new Fixture(window, Proposal()) { FailWrite = true };
                await f.Send("円柱を作って"); await f.Send("実行して"); await f.Send("実行して"); Check(f.Writes.Count == 1);
            });
            await Test("Summary cannot enqueue further operations", async () =>
            {
                using var f = new Fixture(window, Proposal(), Proposal());
                await f.Send("円柱を作って"); await f.Send("実行して"); await f.Send("実行して"); Check(f.Writes.Count == 1);
            });
            await Test("Inspection proposes sketch changes and waits for text approval", async () =>
            {
                var plan = new ChatReply("寸法を追加します。", [new("sketch fully-define", [new("sketch", "Sketch1"), new("expected", new string('A', 64))])]);
                using var f = new Fixture(window, new ChatReply("確認します。", [new("sketch inspect", [])]), plan);
                await f.Send("既存スケッチを完全定義して"); Check(f.Writes.Count == 0 && f.Text.Contains("実行して"));
                await f.Send("実行して"); Check(f.Writes.Count == 1);
                await f.Send("実行して"); Check(f.Writes.Count == 1);
            });
            Console.WriteLine($"{count - failures}/{count} UI workflow checks passed"); window.Close();
        };
        Application.Run(window); return failures == 0 ? 0 : 1;
    }
    static IEnumerable<Control> Descendants(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Descendants));
    static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static ChatReply Proposal() => new("直径10mm、長さ30mmの円柱を作成します。", [new("part create-cylinder", [new("diameter", "10"), new("length", "30")])]);
    sealed class Fixture : IDisposable
    {
        public ChatPanel Panel { get; }
        public List<bool> Writes { get; } = [];
        public string Title = "Part1";
        public bool FailWrite;
        public Fixture(Form form, params ChatReply[] replies)
        {
            Panel = new ChatPanel(123, new Provider(replies), (c, overwrite) =>
            {
                if (c.Command == "info") return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { Connected = true, Title, Path = "", Type = 1 }));
                if (ChatIntents.IsReadOnly(c)) return Task.FromResult("{\"Sketches\":[]}");
                Writes.Add(overwrite);
                if (FailWrite) throw new IOException("Fixture failure");
                return Task.FromResult("{\"Saved\":\"fixture.SLDPRT\"}");
            });
            form.Controls.Add(Panel); Panel.BringToFront();
        }
        T Field<T>(string name) => (T)typeof(ChatPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Panel)!;
        public string Text => Field<RichTextBox>("_conversation").Text;
        public async Task Send(string value)
        {
            Field<TextBox>("_input").Text = value;
            await (Task)typeof(ChatPanel).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Panel, null)!;
        }
        public void Dispose() => Panel.Dispose();
    }
    sealed class Provider(IEnumerable<ChatReply> replies) : IChatProvider
    {
        private readonly Queue<ChatReply> _replies = new(replies);
        public Task<ChatReply> ReplyAsync(ChatSettings settings, IReadOnlyList<ChatMessage> history, string context, CancellationToken token)
            => Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : new ChatReply("完了しました。", []));
    }
}
