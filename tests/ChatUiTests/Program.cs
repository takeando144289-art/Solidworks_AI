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
        if (args.FirstOrDefault() == "--model-picker-live") return ModelPickerLive(args[1]);
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
            await Test("Only Codex and Claude appear in provider and connection controls", async () =>
            {
                using var f = new Fixture(window);
                var choice = f.Field<ComboBox>("_choice");
                Check(choice.Items.Cast<AiProvider>().SequenceEqual(new[] { AiProvider.Codex, AiProvider.Claude }));
                choice.SelectedItem = AiProvider.Claude;
                Check(f.Field<ChatSettings>("_settings").Provider == AiProvider.Claude);
                choice.SelectedItem = AiProvider.Codex;
                Check(f.Field<ChatSettings>("_settings").Provider == AiProvider.Codex);
                var legacy = new ChatSettings { Provider = AiProvider.Gemini };
                using var dialog = new SettingsDialog(legacy);
                var labels = Descendants(dialog).OfType<Label>().Select(x => x.Text).ToArray();
                Check(labels.Contains("Codex CLI") && labels.Contains("Claude CLI") && !labels.Any(x => x.Contains("Gemini")));
                Check(dialog.Settings.Provider == AiProvider.Codex && legacy.Provider == AiProvider.Gemini);
                await Task.CompletedTask;
            });
            await Test("User request reaches native session verbatim without approval", async () =>
            {
                using var f = new Fixture(window);
                await f.Send("  この部品の図面を作って\n寸法も入れて。  ");
                Check(typeof(ChatPanel).GetConstructor([typeof(int), typeof(Func<ChatSettings, int, IAgentSession>)]) is not null);
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
                        var picker = (ModelSelectionDialog)dialog;
                        var list = Descendants(picker).OfType<ListBox>().Single();
                        list.SelectedItem = list.Items.Cast<AgentModel>().Single(x => x.Id == "fixture-model");
                        Descendants(picker).OfType<Button>().Single(x => x.Text == "選択して保存").PerformClick();
                    });
                    await Task.Yield();
                    Check(f.Field<TextBox>("_model").Text == "fixture-model");
                }
                await f.Send("モデル変更後の会話");
                Check(f.Field<ChatSettings>("_settings").Providers[f.Field<ChatSettings>("_settings").Provider].Model == "fixture-model");
            });
            await Test("Model picker preserves unknown model on failure and supports retry/default", async () =>
            {
                int calls = 0;
                using var dialog = new ModelSelectionDialog(AiProvider.Codex, "existing-model", _ =>
                    ++calls == 1 ? Task.FromException<IReadOnlyList<AgentModel>>(new IOException("fixture offline"))
                    : Task.FromResult<IReadOnlyList<AgentModel>>([new("fixture-model", "Fixture", "Description")]));
                dialog.Show(window); await WaitUntil(() => calls > 0);
                Check(dialog.SelectedModel == "existing-model" && Descendants(dialog).OfType<Label>().Any(x => x.Text.Contains("fixture offline")));
                Descendants(dialog).OfType<Button>().Single(x => x.Text == "一覧を再取得").PerformClick(); await Task.Yield();
                var list = Descendants(dialog).OfType<ListBox>().Single();
                Check(list.Items.Cast<AgentModel>().Any(x => x.Id == "fixture-model"));
                list.SelectedIndex = 0; Check(dialog.SelectedModel == ""); dialog.Close();
            });
            await Test("Closing the model picker cancels pending discovery without changing the selection", async () =>
            {
                bool cancelled = false, started = false;
                using var dialog = new ModelSelectionDialog(AiProvider.Codex, "existing-model", async token =>
                {
                    try { started = true; await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled = true; throw; }
                    return Array.Empty<AgentModel>();
                });
                dialog.Show(window); await WaitUntil(() => started); dialog.Close(); await WaitUntil(() => cancelled);
                Check(cancelled);
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
            await Test("File drop attaches a snapshot, sends it with the prompt and preserves original", async () =>
            {
                using var f = new Fixture(window); using var files = new AttachmentFixture(f);
                await f.DropFiles(files.Source);
                Check(f.Field<List<ChatAttachment>>("_attachments").Count == 1 && f.Field<FlowLayoutPanel>("_attachmentList").Visible);
                Check(f.Session.Messages.Count == 0);
                await f.Send("この図面をモデル化して");
                var sent = f.Session.Inputs.Single();
                Check(sent.Text == "この図面をモデル化して" && sent.Attachments.Count == 1);
                Check(File.ReadAllText(sent.Attachments[0].Path) == "reference" && File.ReadAllText(files.Source) == "reference");
                Check(f.Field<List<ChatAttachment>>("_attachments").Count == 0 && f.Text.Contains("参考 図面.txt"));
            });
            await Test("Removing an attachment prevents it from being sent", async () =>
            {
                using var f = new Fixture(window); using var files = new AttachmentFixture(f);
                await f.DropFiles(files.Source);
                var chip = f.Field<FlowLayoutPanel>("_attachmentList").Controls[0];
                chip.Controls.OfType<Button>().Single().PerformClick();
                await f.Send("テキストだけ");
                Check(f.Session.Inputs.Single().Attachments.Count == 0 && File.Exists(files.Source));
            });
            await Test("Failed send retains attachments and draft for retry", async () =>
            {
                using var f = new Fixture(window); using var files = new AttachmentFixture(f);
                await f.DropFiles(files.Source);
                f.Session.Run = (_, _, _, _) => throw new IOException("fixture send failed");
                await f.Send("図面から作って");
                Check(f.Input.Text == "図面から作って" && f.Field<List<ChatAttachment>>("_attachments").Count == 1);
                Check(f.Text.Contains("fixture send failed"));
            });
            await Test("Attachment-only message works and new chat clears unsent attachments", async () =>
            {
                using var f = new Fixture(window); using var files = new AttachmentFixture(f);
                await f.DropFiles(files.Source); await f.Send("");
                Check(f.Session.Inputs.Single().Attachments.Count == 1);
                await f.DropFiles(files.Source); f.Field<Button>("_newChat").PerformClick(); await Task.Yield();
                Check(f.Field<List<ChatAttachment>>("_attachments").Count == 0);
            });
            await Test("User bubbles align right and assistant text remains left at narrow and wide sizes", async () =>
            {
                using var f = new Fixture(window);
                await f.Send("プレートを作って\n幅150 mm、高さ20 mmです。");
                var conversation = f.Field<ConversationView>("_conversation");
                foreach (var width in new[] { 320, 620 })
                {
                    window.Width = width; await Task.Yield();
                    var bubbles = conversation.Controls.OfType<MessageBubble>().ToArray();
                    var user = bubbles.Single(x => x.IsUser); var ai = bubbles.Last();
                    Check(user.Left > ai.Left && user.Right <= conversation.ClientSize.Width);
                    Check(user.BackColor != ai.BackColor && user.MessageText.Contains("幅150 mm"));
                    Check(user.Controls.OfType<Label>().All(x => !x.Visible) && ai.Controls.OfType<Label>().All(x => !x.Visible));
                    Check(bubbles.Zip(bubbles.Skip(1)).All(x => x.First.Bottom < x.Second.Top));
                }
                window.Width = 420;
            });
            await Test("Long wrapped text expands, remains selectable and shrinks after widening", async () =>
            {
                using var f = new Fixture(window);
                var conversation = f.Field<ConversationView>("_conversation");
                var text = string.Concat(Enumerable.Repeat("長い指示：寸法を入れてスケッチを完全定義してください。", 30));
                var bubble = conversation.AddMessage("あなた", text);
                window.Width = 320; await Task.Yield(); var narrow = bubble.Height;
                window.Width = 620; await Task.Yield(); Check(bubble.Height < narrow);
                var body = bubble.Controls.OfType<RichTextBox>().Single(); body.SelectAll();
                Check(body.SelectedText == text && body.ReadOnly);
                // RichEdit reports the actual final line position; all text must fit without an inner scrollbar.
                var last = body.GetPositionFromCharIndex(body.TextLength - 1);
                Check(last.Y + body.Font.Height <= body.ClientSize.Height && body.GetPositionFromCharIndex(0).Y >= 0);
                window.Width = 420;
            });
            await Test("Mouse wheel over message text scrolls history and streaming preserves reading position", async () =>
            {
                using var f = new Fixture(window); var conversation = f.Field<ConversationView>("_conversation"); conversation.Clear();
                MessageBubble? last = null;
                for (var i = 0; i < 20; i++) last = conversation.AddMessage("Codex", "過去の返答 " + i + "\n寸法と拘束を確認しました。");
                var body = last!.Controls.OfType<RichTextBox>().Single(); var before = conversation.VerticalScroll.Value;
                SendMessage(body.Handle, 0x020A, 120 << 16, 0); await Task.Yield();
                Check(conversation.VerticalScroll.Value < before);
                conversation.AutoScrollPosition = new Point(0, 100); var reading = conversation.VerticalScroll.Value;
                conversation.UpdateMessage(last, last.MessageText + "\n追加の出力です。");
                Check(conversation.VerticalScroll.Value == reading);
            });
            await Test("Interleaved stream items keep their own bubbles and final content", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, display, _, _) =>
                {
                    display(new("delta", "前", "one")); display(new("delta", "別の返答", "two"));
                    display(new("delta", "後", "one")); display(new("message", "前後（確定）", "one"));
                    return Task.CompletedTask;
                };
                await f.Send("確認して");
                var bubbles = f.Field<ConversationView>("_conversation").Controls.OfType<MessageBubble>().ToArray();
                Check(bubbles.Count(x => x.MessageText == "前後（確定）") == 1);
                Check(bubbles.Count(x => x.MessageText == "別の返答") == 1);
            });
            await Test("Completion notifies once per turn, never for progress or stream chunks", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, display, _, _) =>
                {
                    display(new("status", "実行中")); display(new("delta", "でき", "one"));
                    display(new("delta", "ました", "one")); display(new("message", "できました", "one"));
                    Check(f.Notifier.Notifications.Count == 0); return Task.CompletedTask;
                };
                await f.Send("作って"); Check(f.Notifier.Notifications.SequenceEqual(new[] { ChatNotification.Completed }));
            });
            await Test("Reply notification comes before waiting, completion only after the answer", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = async (_, _, ask, token) => { await ask(new("穴径は？", []), token); };
                var task = f.Send("穴を追加して");
                Check(f.Notifier.Notifications.SequenceEqual(new[] { ChatNotification.ReplyRequired }));
                await f.Send("3 mm"); await task;
                Check(f.Notifier.Notifications.SequenceEqual(new[] { ChatNotification.ReplyRequired, ChatNotification.Completed }));
            });
            await Test("Cancelled and failed turns never announce successful completion", async () =>
            {
                using var f = new Fixture(window);
                f.Session.Run = (_, _, _, token) => Task.Delay(Timeout.Infinite, token);
                var task = f.Send("作って"); await f.Send("やめて"); await task;
                Check(f.Notifier.Notifications.Count == 0);
                f.Session.Run = (_, _, _, _) => throw new IOException("fixture connection error");
                await f.Send("再試行"); Check(f.Notifier.Notifications.SequenceEqual(new[] { ChatNotification.Failed }));
            });
            await Test("Notification failure cannot break chat, and disposal releases notifier", async () =>
            {
                var f = new Fixture(window); f.Notifier.ThrowOnNotify = true;
                await f.Send("通知失敗時"); Check(f.Text.Contains("native reply") && !f.Text.Contains("接続エラー"));
                f.Dispose(); Check(f.Notifier.Disposed);
            });
            if (args.Contains("--notification-smoke"))
            {
                await Test("Windows notification lifecycle works under the native host", async () =>
                {
                    using var notifier = new ChatNotifier(Environment.ProcessId, () => { });
                    notifier.Notify(ChatNotification.Completed, "通知テスト");
                    await Task.Delay(1000);
                });
            }
            var screenshotIndex = Array.IndexOf(args, "--screenshot");
            if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
            {
                using var f = new Fixture(window); window.Size = new(480, 850);
                var conversation = f.Field<ConversationView>("_conversation"); conversation.Clear();
                conversation.AddMessage("あなた", "この図面をモデル化してください。\n縦20 × 横150 × 厚さ3 mm、四隅はR5にしたいです。\n添付：プレート図面.png");
                conversation.AddMessage("Codex", "図面を確認しました。プレートを作成し、四隅にR5のフィレットを追加します。\n\nスケッチには寸法と拘束を追加して、完全定義されていることを確認します。");
                conversation.AddMessage("あなた", "ねじ穴も追加してください。10 mm間隔でお願いします。");
                conversation.AddMessage("Codex", "ねじ穴の配置を確認します。\n穴の呼び径と、端から最初の穴までの距離を教えてください。");
                f.Field<Label>("_status").Text = "AIからの確認に、このチャットで返信してください";
                window.Refresh();
                using var bitmap = new Bitmap(f.Panel.Width, f.Panel.Height);
                f.Panel.DrawToBitmap(bitmap, f.Panel.ClientRectangle);
                bitmap.Save(Path.GetFullPath(args[screenshotIndex + 1]), System.Drawing.Imaging.ImageFormat.Png);
            }
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
    static int ModelPickerLive(string screenshot)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var settings = ChatSettings.Load();
        using var dialog = new ModelSelectionDialog(settings.Provider, settings.Providers[settings.Provider].Model,
            token => ModelCatalog.ListAsync(settings, token));
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        int ticks = 0, result = 1;
        timer.Tick += (_, _) =>
        {
            var list = Descendants(dialog).OfType<ListBox>().Single();
            var loading = (bool)typeof(ModelSelectionDialog).GetField("_loading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            if (loading && ++ticks < 400) return;
            timer.Stop();
            foreach (AgentModel model in list.Items) Console.WriteLine(model.Id + " | " + model.DisplayName);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(screenshot);
            result = !loading && list.Items.Count > 2 ? 0 : 1;
            foreach (var label in Descendants(dialog).OfType<Label>()) Console.WriteLine(label.Text);
            dialog.Close();
        };
        dialog.Shown += (_, _) => timer.Start(); Application.Run(dialog); return result;
    }
    static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++) await Task.Delay(10);
        Check(predicate());
    }
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
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
        public FakeNotifier Notifier { get; } = new();
        public Fixture(Form form)
        {
            Panel = new ChatPanel(123, (_, _) => { Creations++; return Session; }, Notifier, (_, _) => Task.FromResult<IReadOnlyList<AgentModel>>([new("fixture-model", "Fixture model", "Test model")]));
            form.Controls.Add(Panel); Panel.BringToFront();
        }
        public T Field<T>(string name) => (T)typeof(ChatPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Panel)!;
        public async Task DropFiles(string path)
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
            typeof(Control).GetMethod("OnDragEnter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Input, [args]);
            Check(args.Effect == DragDropEffects.Copy);
            typeof(Control).GetMethod("OnDragDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Input, [args]);
            for (var i = 0; i < 200 && Field<bool>("_importing"); i++) await Task.Delay(10);
            Check(!Field<bool>("_importing"));
        }
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
        public string Text => Field<ConversationView>("_conversation").TranscriptText;
        public async Task Send(string value)
        {
            Field<TextBox>("_input").Text = value;
            await (Task)typeof(ChatPanel).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Panel, null)!;
        }
        public void Dispose() => Panel.Dispose();
    }
    sealed class AttachmentFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-ui-attachment-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "参考 図面.txt");
        public AttachmentFixture(Fixture fixture)
        {
            Directory.CreateDirectory(_root); File.WriteAllText(Source, "reference");
            fixture.Field<ChatSettings>("_settings").ProjectDirectory = _root;
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
    sealed class FakeNotifier : IChatNotifier
    {
        public List<ChatNotification> Notifications { get; } = [];
        public bool ThrowOnNotify, Disposed;
        public void Notify(ChatNotification notification, string provider)
        {
            if (ThrowOnNotify) throw new InvalidOperationException("fixture notification failure");
            Notifications.Add(notification);
        }
        public void Dispose() => Disposed = true;
    }
    sealed class FakeSession : IAgentSession
    {
        public List<string> Messages { get; } = [];
        public List<AgentMessage> Inputs { get; } = [];
        public Task SendAsync(AgentMessage message, Action<AgentEvent> display, Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token)
        {
            Inputs.Add(message); return SendAsync(message.Text, display, ask, token);
        }
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
