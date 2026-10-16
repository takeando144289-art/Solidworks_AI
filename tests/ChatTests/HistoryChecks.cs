using System.Text.Json;
using SolidWorksAgent.Chat;

internal static class HistoryChecks
{
    public static async Task<int> Run()
    {
        int failed = 0;
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        await Test("Chat snapshots persist sessions, drafts and attachments and isolate project paths", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sw-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ChatHistoryStore(root);
                var record = new ChatRecord { Provider = AiProvider.Claude, Draft = "続き", Messages = [new("あなた", "日本語の指示")],
                    Sessions = { [AiProvider.Codex] = "codex-id", [AiProvider.Claude] = "claude-id" },
                    Models = { [AiProvider.Claude] = "sonnet" }, Attachments = [new("図面.pdf", "C:\\references\\図面.pdf", 12)] };
                store.Save(record); var loaded = store.Load(record.Id);
                Check(loaded.Sessions.Count == 2 && loaded.Draft == "続き" && loaded.Models[AiProvider.Claude] == "sonnet" && loaded.Attachments[0].Name == "図面.pdf");
                record.Messages.Add(new("Claude", "完成")); store.Save(record);
                Check(store.List().Single().Messages.Count == 2 && Directory.GetFiles(root).Length == 1);
                File.WriteAllText(Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"), "broken");
                Check(store.List().Count == 1);
                Check(ChatHistoryStore.ForProject("C:\\Project").DirectoryPath == ChatHistoryStore.ForProject("c:\\project\\").DirectoryPath);
                Check(ChatHistoryStore.ForProject("C:\\Other").DirectoryPath != ChatHistoryStore.ForProject("C:\\Project").DirectoryPath);
                try { store.Load("../other"); throw new Exception("Expected invalid id"); } catch (InvalidDataException) { }
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await Test("Native transcript import excludes other projects and subagents, removes integration text and deduplicates", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sw-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                var codex = Path.Combine(root, "codex", "sessions"); var claude = Path.Combine(root, "claude", "projects", "project");
                Directory.CreateDirectory(codex); Directory.CreateDirectory(claude);
                var meta = """{"type":"session_meta","payload":{"id":"saved-codex","cwd":"C:\\project","originator":"solidworks_chat"}}""";
                var user = """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"図面を作って"}]}}""";
                File.WriteAllLines(Path.Combine(codex, "normal.jsonl"), [meta, user]);
                File.WriteAllLines(Path.Combine(codex, "other.jsonl"), [meta.Replace("C:\\\\project", "C:\\\\other"), user]);
                File.WriteAllLines(Path.Combine(codex, "child.jsonl"), [meta.Replace("\"originator\"", "\"parent_thread_id\":\"parent\",\"originator\""), user]);
                var settings = new ChatSettings();
                File.WriteAllText(Path.Combine(claude, "normal.jsonl"), JsonSerializer.Serialize(new { type = "user", cwd = "C:\\project", sessionId = "saved-claude", message = new { content = AgentSession.Instructions(settings, 1) + "\n\n穴を追加して" } }));
                var store = new ChatHistoryStore(Path.Combine(root, "store"));
                Check(NativeHistoryImport.Import(store, "C:\\project", CancellationToken.None, Path.Combine(root, "codex"), Path.Combine(root, "claude")) == 2);
                Check(NativeHistoryImport.Import(store, "C:\\project", CancellationToken.None, Path.Combine(root, "codex"), Path.Combine(root, "claude")) == 0);
                Check(store.List().Single(x => x.Provider == AiProvider.Claude).Messages.Single().Text == "穴を追加して");
                Check(File.Exists(Path.Combine(codex, "normal.jsonl")));
            }
            finally { Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await Test("Claude catalog uses native values, labels and descriptions without duplicates", () =>
        {
            using var doc = JsonDocument.Parse("""{"models":[{"value":"sonnet","displayName":"Sonnet","description":"native description"},{"value":"sonnet"},{"value":"opus","displayName":"Opus"},{"value":"default"},{}]}""");
            var models = ModelCatalog.ReadClaudeModels(doc.RootElement);
            Check(models.Count == 2 && models[0].Id == "sonnet" && models[0].Description == "native description");
            var args = NativeCliSession.Arguments(AiProvider.Claude, "", "saved-session");
            Check(args[Array.IndexOf(args, "--model") + 1] == "default");
            return Task.CompletedTask;
        });
        return failed;
    }
    public static int ImportLive()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-import-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChatHistoryStore(root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var count = NativeHistoryImport.Import(store, ChatSettings.Load().ProjectDirectory, timeout.Token);
            Console.WriteLine("Imported to temporary test store: " + count);
            foreach (var provider in ChatSettings.AvailableProviders)
                Console.WriteLine(provider + " conversations: " + store.List().Count(x => x.Provider == provider));
            Check(count > 0); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static async Task<int> Live()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var settings = ChatSettings.Load();
        var root = Path.Combine(Path.GetTempPath(), "sw-history-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        settings.ProjectDirectory = root;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            settings.Provider = AiProvider.Claude;
            var models = await ModelCatalog.ListAsync(settings, timeout.Token);
            Console.WriteLine("Claude models: " + string.Join(", ", models.Select(x => x.DisplayName)));
            Check(models.Count > 0);
            foreach (var provider in ChatSettings.AvailableProviders)
            {
                settings.Provider = provider;
                var secret = "CHECK_" + Guid.NewGuid().ToString("N")[..10];
                string? id;
                await using (var first = AgentSession.Create(settings, 0))
                {
                    await first.SendAsync("This is a chat persistence test only. Do not use any tools, read files, or operate CAD. Remember this test word: " + secret + ". Reply with OK only.",
                        _ => { }, (_, _) => Task.FromResult("拒否"), timeout.Token);
                    id = first.SessionId; Check(!string.IsNullOrEmpty(id));
                }
                var messages = new List<AgentEvent>();
                await using var resumed = AgentSession.Create(settings, 0);
                resumed.RestoreSession(id!);
                await resumed.SendAsync("This is still only a chat persistence test. Do not use tools or operate CAD. Reply with the test word I gave you in the previous message, and nothing else.",
                    messages.Add, (_, _) => Task.FromResult("拒否"), timeout.Token);
                Check(messages.Any(x => x.Kind == "message" && x.Text.Contains(secret)));
                Console.WriteLine("PASS " + provider + " resumed native conversation after process disposal, recalled prior message");
            }
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
}
