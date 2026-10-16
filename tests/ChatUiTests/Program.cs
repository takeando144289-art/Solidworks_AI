using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorksAgent.Chat;
using SolidWorksAgent.ChatUi;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
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
            await Test("Enter in focused input sends once without a button click", async () =>
            {
                using var f = new Fixture(window);
                f.Input.Text = "Enterで送る";
                await f.PressEnter(false);
                Check(f.Session.Messages.SequenceEqual(new[] { "Enterで送る" }));
                Check(f.Input.Text == "");
            });
            await Test("Shift Enter inserts a newline at the caret without sending", async () =>
            {
                using var f = new Fixture(window);
                f.Input.Text = "前後"; f.Input.Select(1, 0);
                await f.PressEnter(true);
                Check(f.Input.Text == "前\r\n後" && f.Session.Messages.Count == 0);
                await f.PressEnter(false);
                Check(f.Session.Messages.Single() == "前\r\n後");
            });
            await Test("IME confirmation Enter does not send; a subsequent Enter does", async () =>
            {
                using var f = new Fixture(window);
                foreach (var release in new[] { (Message: 0x0101U, Key: 13), (Message: 0x0291U, Key: 13), (Message: 0x0101U, Key: 229) })
                {
                    var previous = f.Session.Messages.Count;
                    f.Input.Text = "日本語"; f.Input.Focus();
                    SendMessage(f.Input.Handle, 0x010D, 0, 0); // WM_IME_STARTCOMPOSITION
                    SendMessage(f.Input.Handle, 0x0100, 13, 1);
                    SendMessage(f.Input.Handle, 0x010E, 0, 0); // WM_IME_ENDCOMPOSITION
                    SendMessage(f.Input.Handle, 0x0100, 13, 1); // same confirmation key
                    SendMessage(f.Input.Handle, release.Message, release.Key, unchecked((nint)0xC0000001));
                    Check(f.Session.Messages.Count == previous && f.Input.Text == "日本語");
                    await f.PressEnter(false);
                    Check(f.Session.Messages.Count == previous + 1 && f.Session.Messages.Last() == "日本語");
                }
            });
            await Test("Shift Enter replaces a selection and empty Enter does not send", async () =>
            {
                using var f = new Fixture(window);
                await f.PressEnter(false); Check(f.Session.Messages.Count == 0);
                f.Input.Text = "前選択後"; f.Input.Select(1, 2);
                await f.PressEnter(true);
                Check(f.Input.Text == "前\r\n後" && f.Input.SelectionStart == 3 && f.Session.Messages.Count == 0);
            });
            await Test("Model button opens dialog and saves selection", async () =>
            {
                using var f = new Fixture(window);
                for (var i = 0; i < 3; i++)
                {
                    f.ClickDialogButton("_selectModel", dialog =>
                    {
                        dialog.Controls.OfType<TextBox>().Single().Text = "fixture-model";
                        dialog.Controls.OfType<Button>().Single().PerformClick();
                    });
                    await Task.Yield();
                    Check(f.Field<TextBox>("_model").Text == "fixture-model");
                }
                await f.Send("モデル変更後の会話");
                Check(f.Field<ChatSettings>("_settings").Providers[f.Field<ChatSettings>("_settings").Provider].Model == "fixture-model");
            });
            await Test("Connection settings can open and cancel without losing conversation", async () =>
            {
                using var f = new Fixture(window); await f.Send("続けて");
                f.ClickDialogButton("_configure", dialog => dialog.Close());
                await f.Send("設定を閉じた後");
                Check(f.Creations == 1 && f.Session.Messages.Count == 2);
            });
            await Test("New chat button resets session and remains usable", async () =>
            {
                using var f = new Fixture(window); await f.Send("最初");
                f.Field<Button>("_newChat").PerformClick(); await Task.Yield();
                Check(f.Text.Contains("新しい会話を開始しました"));
                await f.Send("次"); Check(f.Creations == 2);
            });
            await Test("Async settings failure stays in chat without escaping to host", async () =>
            {
                using var f = new Fixture(window); await f.Send("最初");
                f.Session.DisposeError = new IOException("fixture cleanup failed");
                f.Field<Button>("_newChat").PerformClick(); await Task.Delay(50);
                Check(f.Text.Contains("設定エラー") && f.Text.Contains("fixture cleanup failed"));
                f.Session.DisposeError = null;
                await f.Send("エラー後も会話");
                Check(f.Session.Messages.Count == 2);
            });
            Console.WriteLine($"{count - failures}/{count} UI workflow checks passed"); window.Close();
        };
        if (args.Contains("--native-host"))
        {
            // SOLIDWORKS supplies a native message loop, not Application.Run.
            window.Show();
            while (!window.IsDisposed && GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (!IsDialogMessage(window.Handle, ref message))
                { TranslateMessage(in message); DispatchMessage(in message); }
            }
        }
        else Application.Run(window);
        return failures == 0 ? 0 : 1;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public System.Drawing.Point Point; public uint Private; }
    [DllImport("user32.dll")] static extern int GetMessage(out NativeMessage message, nint window, uint first, uint last);
    [DllImport("user32.dll")] static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool IsDialogMessage(nint window, ref NativeMessage message);
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
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
        public T Field<T>(string name) => (T)typeof(ChatPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Panel)!;
        public TextBox Input => Field<TextBox>("_input");
        public async Task PressEnter(bool shift)
        {
            Check(Input.Focus());
            var saved = new byte[256]; Check(GetKeyboardState(saved));
            var state = (byte[])saved.Clone();
            state[0x10] = state[0xA0] = shift ? (byte)0x80 : (byte)0;
            state[0x11] = state[0x12] = 0;
            try
            {
                Check(SetKeyboardState(state));
                Check(PostMessage(Input.Handle, 0x100, 13, 1));
                Check(PostMessage(Input.Handle, 0x101, 13, unchecked((nint)0xC0000001)));
                await Task.Delay(80);
            }
            finally { SetKeyboardState(saved); }
        }
        public void ClickDialogButton(string name, Action<Form> act)
        {
            bool seen = false;
            Exception? error = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 30 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Modal);
                if (dialog is null) return;
                timer.Stop(); seen = true;
                try { act(dialog); }
                catch (Exception ex) { error = ex; dialog.Close(); }
            };
            timer.Start(); Field<Button>(name).PerformClick(); timer.Stop();
            if (error is not null) throw error;
            Check(seen);
        }
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
        public Exception? DisposeError;
        public async ValueTask DisposeAsync()
        {
            if (DisposeError is not null) { await Task.Yield(); throw DisposeError; }
        }
    }
}
