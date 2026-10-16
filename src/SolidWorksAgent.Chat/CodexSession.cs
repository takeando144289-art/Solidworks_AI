using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolidWorksAgent.Chat;

/// <summary>Codex's native app-server owns conversation, tools, planning, retries and completion.</summary>
public sealed class CodexSession(ChatSettings settings, int processId,
    Func<JsonElement, CancellationToken, Task<ProcessResult>>? toolOverride = null) : IAgentSession
{
    private readonly NativeCadTool _cad = new(settings, processId);
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _questions = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly ConcurrentDictionary<long, Task> _serverCalls = new();
    private Process? _process;
    private Task? _reader, _stderr;
    private string? _thread, _turn;
    private long _next;
    private TaskCompletionSource? _finished;
    private Action<AgentEvent>? _display;
    private Func<AgentQuestion, CancellationToken, Task<string>>? _ask;
    private CancellationToken _turnToken;
    private string _diagnostics = "";
    private bool _disposed, _initialized, _modelOverrideActive;

    public Task SendAsync(string message, Action<AgentEvent> display,
        Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token) =>
        SendAsync(new AgentMessage(message), display, ask, token);

    public async Task SendAsync(AgentMessage message, Action<AgentEvent> display,
        Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished is not null) throw new InvalidOperationException("Codex is already running a turn.");
        _display = display; _ask = ask; _turnToken = token;
        try
        {
            await StartAsync(token);
            _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _turn = null;
            token.ThrowIfCancellationRequested();
            var model = await ResolveTurnModelAsync(token);
            var result = await CallAsync("turn/start", new { threadId = _thread, approvalPolicy = "on-request", approvalsReviewer = "auto_review", input = message.ToCodexInput(), model }, CancellationToken.None);
            _modelOverrideActive = !string.IsNullOrWhiteSpace(settings.Providers[AiProvider.Codex].Model);
            _turn = result.GetProperty("turn").GetProperty("id").GetString();
            try { await _finished.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Do not kill the app-server or a CAD child while it is saving. Interrupt its own turn API.
                await _cad.DrainAsync();
                if (_turn is not null) await CallAsync("turn/interrupt", new { threadId = _thread, turnId = _turn }, CancellationToken.None);
                await _finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
                throw;
            }
        }
        finally
        {
            if (!_serverCalls.IsEmpty) await Task.WhenAll(_serverCalls.Values);
            _finished = null; _turn = null; _display = null; _ask = null;
        }
    }

    public Task SteerAsync(string message, CancellationToken token) => SteerAsync(new AgentMessage(message), token);

    public async Task SteerAsync(AgentMessage message, CancellationToken token)
    {
        if (_thread is null || _turn is null) throw new InvalidOperationException("接続の開始処理中です。しばらくしてから送信してください。");
        await CallAsync("turn/steer", new { threadId = _thread, expectedTurnId = _turn, input = message.ToCodexInput() }, token);
    }

    private async Task StartServerAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            if (_process.HasExited) throw new IOException("Codex接続が終了しています。新しい会話を開始してください。\n" + _diagnostics);
            if (!_initialized) throw new IOException("Codexの初期化に失敗しています。新しい会話を開始してください。");
            return;
        }
        if (!Directory.Exists(settings.ProjectDirectory)) throw new DirectoryNotFoundException("Windows側の作業フォルダーを設定してください。");
        var (exe, prefix) = CliAiProvider.ResolveExecutable(AiProvider.Codex, settings.Providers[AiProvider.Codex].Executable, settings.NodeExecutable);
        // Preserve the user's Windows Codex configuration. No global config or WSL setting is written.
        _process = new() { StartInfo = ProcessRunner.StartInfo(exe, prefix.Concat(["app-server", "--listen", "stdio://"]), settings.ProjectDirectory) };
        if (!_process.Start()) throw new IOException("Codex app-serverを起動できませんでした。");
        _reader = ReadAsync(); _stderr = ReadErrorsAsync();
        await CallAsync("initialize", new { clientInfo = new { name = "solidworks_chat", title = "SOLIDWORKS Chat", version = "7.0" }, capabilities = new { experimentalApi = true } }, token);
        await WriteAsync(new { method = "initialized", @params = new { } });
        _initialized = true;
    }

    public async Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken token)
    {
        await StartServerAsync(token);
        return await ModelCatalog.ReadPagesAsync((cursor, ct) => CallAsync("model/list",
            new { cursor, limit = 100, includeHidden = false }, ct), token);
    }

    private async Task StartAsync(CancellationToken token)
    {
        await StartServerAsync(token);
        if (_thread is not null) return;
        // Let Codex review eligible escalation requests itself; keep its sandbox and policy checks.
        var result = await CallAsync("thread/start", new
        {
            cwd = settings.ProjectDirectory,
            model = string.IsNullOrWhiteSpace(settings.Providers[AiProvider.Codex].Model) ? null : settings.Providers[AiProvider.Codex].Model,
            sandbox = "workspace-write", approvalPolicy = "on-request", approvalsReviewer = "auto_review",
            developerInstructions = AgentSession.Instructions(settings, processId),
            dynamicTools = new[] { new { type = "function", name = "solidworks_cli", description = "Run the existing Windows SOLIDWORKS CLI in this pane's instance. Pass CLI arguments without --solidworks-pid. --help lists all commands; optional stdin carries API plans. Returns actual exitCode/output/error. No host retry or command catalog restriction.", inputSchema = JsonNode.Parse("""{"type":"object","properties":{"arguments":{"type":"array","items":{"type":"string"}},"stdin":{"type":["string","null"]}},"required":["arguments","stdin"],"additionalProperties":false}""") } }
        }, token);
        _thread = result.GetProperty("thread").GetProperty("id").GetString();
        _modelOverrideActive = !string.IsNullOrWhiteSpace(settings.Providers[AiProvider.Codex].Model);
        _display?.Invoke(new("status", "Codexに接続しました。"));
    }

    private async Task<string?> ResolveTurnModelAsync(CancellationToken token)
    {
        var selected = settings.Providers[AiProvider.Codex].Model;
        if (!string.IsNullOrWhiteSpace(selected)) return selected;
        if (!_modelOverrideActive) return null;
        // A null turn model keeps the thread's last model. Explicitly resolve the default
        // when the user clears an override, without losing the existing conversation.
        var result = await CallAsync("config/read", new { includeLayers = false, cwd = settings.ProjectDirectory }, token);
        if (result.GetProperty("config").TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(model.GetString())) return model.GetString();
        var models = await ListModelsAsync(token);
        return models.FirstOrDefault(x => x.IsDefault)?.Id
            ?? throw new InvalidOperationException("既定モデルを確認できませんでした。モデル一覧から使用するモデルを選んでください。");
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _next);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
        }
        finally { _calls.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value)
    {
        await _write.WaitAsync();
        try
        {
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value));
            await _process.StandardInput.FlushAsync();
        }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process!.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("method", out var m))
                {
                    var method = m.GetString()!;
                    var p = root.GetProperty("params");
                    if (root.TryGetProperty("id", out var requestId))
                    {
                        var key = Interlocked.Increment(ref _next);
                        var task = HandleRequestAsync(requestId, method, p);
                        _serverCalls[key] = task;
                        _ = task.ContinueWith(t => { _serverCalls.TryRemove(key, out _); if (t.IsFaulted) _finished?.TrySetException(t.Exception!.GetBaseException()); }, TaskScheduler.Default);
                    }
                    else Notify(method, p);
                }
                else if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && _calls.TryGetValue(id.GetInt64(), out var call))
                {
                    if (root.TryGetProperty("error", out var error)) call.TrySetException(new InvalidOperationException(error.GetRawText()));
                    else call.TrySetResult(root.GetProperty("result").Clone());
                }
            }
            throw new IOException("Codex app-serverとの接続が終了しました。\n" + _diagnostics);
        }
        catch (Exception ex)
        {
            foreach (var call in _calls.Values) call.TrySetException(ex);
            _finished?.TrySetException(ex);
        }
    }

    private void Notify(string method, JsonElement p)
    {
        if (p.TryGetProperty("threadId", out var tid) && _thread is not null && tid.GetString() != _thread) return;
        if (method == "item/agentMessage/delta")
            _display?.Invoke(new("delta", p.GetProperty("delta").GetString()!, p.GetProperty("itemId").GetString()));
        else if (method == "item/completed" && p.GetProperty("item").GetProperty("type").GetString() == "agentMessage")
        {
            var item = p.GetProperty("item");
            _display?.Invoke(new("message", item.GetProperty("text").GetString()!, item.GetProperty("id").GetString()));
        }
        else if (method == "item/started")
        {
            var item = p.GetProperty("item"); var type = item.GetProperty("type").GetString();
            if (type != "agentMessage") _display?.Invoke(new("status", type switch { "commandExecution" => "Codexがコマンドを実行しています…", "dynamicToolCall" => "CodexがSOLIDWORKSを操作しています…", "fileChange" => "Codexがファイルを編集しています…", _ => "Codexが作業しています…" }));
        }
        else if (method == "turn/completed")
        {
            var turn = p.GetProperty("turn");
            if (turn.GetProperty("status").GetString() == "failed")
                _finished?.TrySetException(new InvalidOperationException(turn.TryGetProperty("error", out var e) ? e.ToString() : "Codex turn failed"));
            else _finished?.TrySetResult();
        }
        else if (method == "error" && p.TryGetProperty("willRetry", out var retry) && retry.ValueKind == JsonValueKind.True)
            _display?.Invoke(new("status", "Codexが接続を再試行しています…"));
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement p)
    {
        object response;
        if (method == "item/tool/call")
        {
            try
            {
                _turnToken.ThrowIfCancellationRequested();
                if (p.GetProperty("tool").GetString() != "solidworks_cli") throw new ArgumentException("Unknown host tool");
                var result = toolOverride is null ? await _cad.ExecuteAsync(p.GetProperty("arguments"), _turnToken)
                    : await toolOverride(p.GetProperty("arguments"), _turnToken);
                response = new { contentItems = new[] { new { type = "inputText", text = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } }, success = result.ExitCode == 0 };
            }
            catch (Exception ex) { response = new { contentItems = new[] { new { type = "inputText", text = ex.Message } }, success = false }; }
        }
        else if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
        {
            var answer = await AskAsync(new("Codexからの承認要求\n" + p.ToString(), ["許可", "拒否"]));
            response = new { decision = answer == "許可" ? "accept" : "decline" };
        }
        else if (method == "item/tool/requestUserInput")
        {
            var answers = new Dictionary<string, object>();
            foreach (var question in p.GetProperty("questions").EnumerateArray())
            {
                var options = question.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array ? opts.EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray() : [];
                var answer = await AskAsync(new(question.GetProperty("question").GetString()!, options));
                answers[question.GetProperty("id").GetString()!] = new { answers = new[] { answer } };
            }
            response = new { answers };
        }
        else if (method == "item/permissions/requestApproval")
        {
            var answer = await AskAsync(new("Codexからの権限要求\n" + p.ToString(), ["許可", "拒否"]));
            response = new { permissions = answer == "許可" ? p.GetProperty("permissions").Clone() : JsonSerializer.SerializeToElement(new { }), scope = "turn" };
        }
        else
        {
            // Unknown server requests receive an explicit protocol error instead of hanging a turn.
            await WriteAsync(new { id, error = new { code = -32601, message = "Unsupported client request: " + method } }); return;
        }
        await WriteAsync(new { id, result = response });
    }

    private async Task<string> AskAsync(AgentQuestion question)
    {
        try
        {
            await _questions.WaitAsync(_turnToken);
            try { return _ask is null ? "拒否" : await _ask(question, _turnToken); }
            finally { _questions.Release(); }
        }
        catch (OperationCanceledException) { return "拒否"; }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync() is { } line)
            {
                var text = _diagnostics + "\n" + line;
                _diagnostics = text.Length > 8000 ? text[^8000..] : text;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await _cad.DrainAsync();
        if (_process is not null)
        {
            try { _process.StandardInput.Close(); if (!_process.HasExited) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { if (!_process.HasExited) _process.Kill(true); }
            finally { _process.Dispose(); }
        }
    }
}
