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
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
            }
            await Test("User request reaches native session verbatim without approval", async () =>
            {
                using var f = new Fixture(window);
                await f.Send("  この部品の図面を作って\n寸法も入れて。  ");
                Check(f.Session.Messages.Single() == "  この部品の図面を作って\n寸法も入れて。  ");
                Check(f.Text.Contains("native reply") && !f.Text.Contains("よければ「実行して」"));
            });
            await Test("Conversation reuses vendor session", async () =>
            {
                using var f = new Fixture(window); await f.Send("図面を作って"); await f.Send("右側面も追加して");
                Check(f.Creations == 1 && f.Session.Messages.Count == 2);
            });
            await Test("No reply JSON plan interpretation or host execution", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, display, _, _) => { display(new("message", "{\"commands\":[{\"command\":\"arbitrary-native-output\"}]}")); return Task.CompletedTask; };
                await f.Send("作って"); Check(f.Text.Contains("arbitrary-native-output") && f.Creations == 1);
            });
            await Test("Streams text once even with final item notification", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, display, _, _) => { display(new("delta", "確認", "one")); display(new("delta", "しました", "one")); display(new("message", "確認しました", "one")); return Task.CompletedTask; };
                await f.Send("確認して"); Check(f.Text.Split("確認しました").Length == 2);
            });
            await Test("Native user-input request uses the same chat", async () =>
            {
                using var f = new Fixture(window); string? answer = null;
                f.Session.Run = async (_, _, ask, token) => { answer = await ask(new("穴径を教えてください", []), token); };
                var task = f.Send("穴を追加して"); Check(!task.IsCompleted);
                await f.Send("3 mm"); await task; Check(answer == "3 mm" && f.Session.Messages.Count == 1);
            });
            await Test("New input during work steers native agent", async () =>
            {
                using var f = new Fixture(window); var done = new TaskCompletionSource();
                f.Session.Run = (_, _, _, _) => done.Task;
                var task = f.Send("図面を作って"); await f.Send("A3にして");
                done.SetResult(); await task; Check(f.Session.Steering.Single() == "A3にして");
            });
            await Test("Stop cancels native turn without another agent turn", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, _, _, token) => Task.Delay(Timeout.Infinite, token);
                var task = f.Send("作成して"); await f.Send("やめて"); await task;
                Check(f.Session.Messages.Count == 1);
            });
            Console.WriteLine($"{count - failures}/{count} UI workflow checks passed"); window.Close();
        };
        Application.Run(window); return failures == 0 ? 0 : 1;
    }
    static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    sealed class Fixture : IDisposable
    {
        public ChatPanel Panel { get; }
        public FakeSession Session { get; } = new();
        public int Creations;
        public Fixture(Form form)
        {
            Panel = new ChatPanel(123, (_, _) => { Creations++; return Session; });
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
    sealed class FakeSession : IAgentSession
    {
        public List<string> Messages { get; } = [];
        public List<string> Steering { get; } = [];
        public Func<string, Action<AgentEvent>, Func<AgentQuestion, CancellationToken, Task<string>>, CancellationToken, Task>? Run;
        public Task SendAsync(string message, Action<AgentEvent> display, Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token)
        {
            Messages.Add(message);
            if (Run is not null) return Run(message, display, ask, token);
            display(new("message", "native reply")); return Task.CompletedTask;
        }
        public Task SteerAsync(string message, CancellationToken token) { Steering.Add(message); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
