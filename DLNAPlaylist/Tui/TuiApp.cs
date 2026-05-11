using System.Threading.Channels;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace DLNAPlaylist.Tui;

/// <summary>
///     主 TUI。Spectre.Console Live 每 ~200ms 刷一次快照。
///     键盘事件通过独立线程读，最终也投进 coordinator 的 Channel。
///     审批：在 Live 外面做 —— 把 Live "暂停"（退出后再重建）、
///     用 AnsiConsole.Prompt 弹 Choice，选完再恢复 Live。
/// </summary>
public sealed class TuiApp : IHostedService
{
    private readonly EventBus<CoordinatorEvent> _bus;
    private readonly ChannelWriter<CoordinatorCommand> _commands;
    private readonly IHostApplicationLifetime _lifetime;

    // 渲染状态快照（UI 线程读，事件线程写，lock 保护）
    private readonly Lock _lock = new();
    private readonly LogSink _log;
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private IReadOnlyList<RemoteDevice> _devices = [];
    private int _deviceSel;
    private FocusPane _focus = FocusPane.Queue;
    private bool _isPaused;
    private IReadOnlyList<LogEntry> _logTail = [];

    private volatile bool _needPauseForPrompt;
    private TimeSpan? _nowDur;
    private QueueItem? _nowPlaying;
    private TimeSpan? _nowPos;
    private IReadOnlyList<PendingApproval> _pending = [];
    private IReadOnlyList<QueueItem> _queue = [];

    // 选中项索引
    private int _queueSel;
    private RemoteDevice? _target;
    private Task? _runTask;

    public TuiApp(EventBus<CoordinatorEvent> bus, LogSink log, ChannelWriter<CoordinatorCommand> commands,
        IHostApplicationLifetime lifetime)
    {
        _bus = bus;
        _log = log;
        _commands = commands;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = Task.Run(async () =>
        {
            try
            {
                await RunAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _lifetime.StopApplication(); // TUI 退出 → 整个 host 跟着停
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // 订阅事件
        var reader = _bus.Subscribe();
        _ = Task.Run(() => EventLoopAsync(reader, ct), ct);
        // 初始日志尾巴
        lock (_lock)
        {
            _logTail = _log.Snapshot();
        }

        // 键盘线程
        _ = Task.Run(() => KeyLoop(ct), ct);

        AnsiConsole.Clear();

        while (!ct.IsCancellationRequested)
        {
            await AnsiConsole.Live(BuildRoot())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async live =>
                {
                    while (!ct.IsCancellationRequested && !_needPauseForPrompt)
                    {
                        live.UpdateTarget(BuildRoot());
                        try
                        {
                            await Task.Delay(200, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }).ConfigureAwait(false);

            if (_needPauseForPrompt)
            {
                await RunApprovalPromptAsync(ct).ConfigureAwait(false);
                _needPauseForPrompt = false;
            }
        }
    }

    private async Task EventLoopAsync(ChannelReader<CoordinatorEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var e in reader.ReadAllAsync(ct).ConfigureAwait(false))
                lock (_lock)
                {
                    switch (e)
                    {
                        case CoordinatorEvent.QueueChanged x:
                            _queue = x.Items;
                            if (_queueSel >= _queue.Count) _queueSel = Math.Max(0, _queue.Count - 1);
                            break;
                        case CoordinatorEvent.DevicesChanged x:
                            _devices = x.Devices;
                            if (_deviceSel >= _devices.Count) _deviceSel = Math.Max(0, _devices.Count - 1);
                            break;
                        case CoordinatorEvent.TargetChanged x: _target = x.Target; break;
                        case CoordinatorEvent.NowPlayingChanged x:
                            _nowPlaying = x.Item;
                            _nowPos = x.Position;
                            _nowDur = x.Duration;
                            _isPaused = x.IsPaused;
                            break;
                        case CoordinatorEvent.ApprovalsChanged x:
                            _pending = x.Pending;
                            if (_pending.Count > 0) _needPauseForPrompt = true;
                            break;
                        case CoordinatorEvent.LogAppended:
                            _logTail = _log.Snapshot();
                            break;
                    }
                }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void KeyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_needPauseForPrompt)
            {
                Thread.Sleep(80);
                continue;
            }

            if (!Console.KeyAvailable)
            {
                Thread.Sleep(40);
                continue;
            }

            var ki = Console.ReadKey(true);
            HandleKey(ki);
        }
    }

    private void HandleKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Q:
                _commands.TryWrite(new CoordinatorCommand.Stop());
                _lifetime.StopApplication();
                break;
            case ConsoleKey.Tab:
                _focus = _focus == FocusPane.Queue ? FocusPane.Devices : FocusPane.Queue; break;

            case ConsoleKey.Spacebar: _commands.TryWrite(new CoordinatorCommand.TogglePause()); break;
            case ConsoleKey.N: _commands.TryWrite(new CoordinatorCommand.Next()); break;
            case ConsoleKey.P: _commands.TryWrite(new CoordinatorCommand.Previous()); break;
            case ConsoleKey.S: _commands.TryWrite(new CoordinatorCommand.Stop()); break;

            case ConsoleKey.UpArrow: MoveSel(-1, (k.Modifiers & ConsoleModifiers.Shift) != 0); break;
            case ConsoleKey.DownArrow: MoveSel(+1, (k.Modifiers & ConsoleModifiers.Shift) != 0); break;

            case ConsoleKey.Enter:
                if (_focus == FocusPane.Devices)
                {
                    lock (_lock)
                    {
                        if (_deviceSel < _devices.Count)
                            _commands.TryWrite(new CoordinatorCommand.SetTarget(_devices[_deviceSel]));
                    }
                }
                else
                {
                    _commands.TryWrite(new CoordinatorCommand.PlayNow());
                }

                break;

            case ConsoleKey.Delete:
                lock (_lock)
                {
                    if (_focus == FocusPane.Queue && _queueSel < _queue.Count)
                        _commands.TryWrite(new CoordinatorCommand.RemoveItem(_queue[_queueSel].Id));
                }

                break;

            case ConsoleKey.C:
                if ((k.Modifiers & ConsoleModifiers.Shift) != 0)
                    _commands.TryWrite(new CoordinatorCommand.ClearQueue());
                break;

            case ConsoleKey.R:
                // Rescan devices：通过一个特殊命令？简单做：要求使用者自然等待 30s 轮询；
                // 或直接在 TUI 打日志提示
                _log.Info("TUI", "设备发现 30s 一次；等待或切换 Tab 可查看最新。");
                break;
        }
    }

    private void MoveSel(int delta, bool shift)
    {
        lock (_lock)
        {
            if (_focus == FocusPane.Queue)
            {
                if (_queue.Count == 0) return;
                if (shift && _queueSel < _queue.Count)
                {
                    // 移动队列项
                    _commands.TryWrite(new CoordinatorCommand.MoveItem(_queue[_queueSel].Id, delta));
                    _queueSel = Math.Clamp(_queueSel + delta, 0, _queue.Count - 1);
                }
                else
                {
                    _queueSel = Math.Clamp(_queueSel + delta, 0, _queue.Count - 1);
                }
            }
            else
            {
                if (_devices.Count == 0) return;
                _deviceSel = Math.Clamp(_deviceSel + delta, 0, _devices.Count - 1);
            }
        }
    }

    // ---------- 渲染 ----------

    private IRenderable BuildRoot()
    {
        Layout layout;
        lock (_lock)
        {
            layout = new Layout("root")
                .SplitRows(
                    new Layout("top").SplitColumns(
                        new Layout("queue") { Ratio = 2 },
                        new Layout("devices") { Ratio = 1 }),
                    new Layout("bottom").SplitColumns(
                        new Layout("now") { Ratio = 1 },
                        new Layout("log") { Ratio = 1 }));

            layout["queue"].Update(RenderQueue());
            layout["devices"].Update(RenderDevices());
            layout["now"].Update(RenderNowPlaying());
            layout["log"].Update(RenderLog());
        }

        return layout;
    }

    private IRenderable RenderQueue()
    {
        var t = new Table().Expand().Border(TableBorder.Rounded);
        t.Title = new TableTitle($" 队列 {(_focus == FocusPane.Queue ? "[bold yellow](当前焦点)[/]" : "")} ");
        t.AddColumn("#");
        t.AddColumn("标题");
        t.AddColumn("来源");
        t.AddColumn("时长");
        t.AddColumn("状态");

        for (var i = 0; i < _queue.Count; i++)
        {
            var q = _queue[i];
            var prefix = i == _queueSel && _focus == FocusPane.Queue ? "[bold on blue]>[/] " : "  ";
            var status = q.Status switch
            {
                QueueItemStatus.Playing => "[green]Playing[/]",
                QueueItemStatus.Paused => "[yellow]Paused[/]",
                QueueItemStatus.Queued => "Queued",
                QueueItemStatus.Failed => "[red]Failed[/]",
                _ => q.Status.ToString()
            };
            t.AddRow(
                $"{prefix}{i + 1}",
                Markup.Escape(q.Title).EllipsisIfLong(40),
                Markup.Escape(q.Source?.Display ?? "-").EllipsisIfLong(30),
                q.Duration is null ? "-" : DlnaTime.Format(q.Duration.Value),
                status);
        }

        if (_queue.Count == 0)
            t.AddRow("", "[grey](空队列；等待 DLNA 投屏...)[/]", "", "", "");
        return new Panel(t).Expand().Header(" 播放队列 ");
    }

    private IRenderable RenderDevices()
    {
        var t = new Table().Expand().Border(TableBorder.Rounded);
        t.AddColumn("");
        t.AddColumn("目标设备");
        t.AddColumn("厂商");
        for (var i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            var isTarget = _target is not null && d.Udn == _target.Udn;
            var marker = i == _deviceSel && _focus == FocusPane.Devices ? "[bold on blue]>[/]" : "";
            var name = isTarget ? $"[green]● {Markup.Escape(d.Display)}[/]" : Markup.Escape(d.Display);
            t.AddRow(marker, name, Markup.Escape(d.Manufacturer).EllipsisIfLong(18));
        }

        if (_devices.Count == 0)
            t.AddRow("", "[grey](扫描中...)[/]", "");
        return new Panel(t).Expand().Header(" 目标设备 (Tab 切焦点, Enter 选定) ");
    }

    private IRenderable RenderNowPlaying()
    {
        var g = new Grid().AddColumn();
        if (_nowPlaying is null)
        {
            g.AddRow(new Markup("[grey]未在播放[/]"));
        }
        else
        {
            g.AddRow(new Markup($"[bold]{Markup.Escape(_nowPlaying.Title)}[/]"));
            g.AddRow(new Markup($"来源: {Markup.Escape(_nowPlaying.Source?.Display ?? "-")}"));
            g.AddRow(new Markup($"目标: {Markup.Escape(_target?.Display ?? "-")}"));
            var pos = _nowPos ?? TimeSpan.Zero;
            var dur = _nowDur ?? _nowPlaying.Duration ?? TimeSpan.Zero;
            var pct = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            var bar = BuildProgressBar(pct, 40);
            g.AddRow(new Markup($"{DlnaTime.Format(pos)} {bar} {DlnaTime.Format(dur)}"));
            g.AddRow(new Markup(_isPaused ? "[yellow]⏸ PAUSED[/]" : "[green]▶ PLAYING[/]"));
        }

        var help = new Markup(
            "[grey]Space=播停 N=下一首 P=重播当前 S=停 Enter=起播/选目标 Del=删 Shift+↑↓=移位 Tab=切焦点 Shift+C=清空 Q=退出[/]");
        var panel = new Panel(new Rows(g, new Rule(), help)).Expand().Header(" 正在播放 ");
        return panel;
    }

    private static string BuildProgressBar(double pct, int width)
    {
        var full = (int)(pct * width);
        return "[" + new string('█', full) + new string('░', width - full) + "]";
    }

    private IRenderable RenderLog()
    {
        var lines = _logTail.TakeLast(20);
        var list = new List<IRenderable>();
        foreach (var e in lines)
        {
            var color = e.Level switch
            {
                LogLevel.Critical => "red",
                LogLevel.Error => "red",
                LogLevel.Warning => "yellow",
                _ => "grey"
            };
            var time = e.At.ToString("HH:mm:ss");
            list.Add(new Markup($"[{color}]{time}[/] [[{Markup.Escape(e.Category)}]] {Markup.Escape(e.Message)}"));
        }

        if (list.Count == 0) list.Add(new Markup("[grey](no log)[/]"));
        return new Panel(new Rows(list)).Expand().Header(" 日志 ");
    }

    // ---------- 审批 ----------

    private async Task RunApprovalPromptAsync(CancellationToken ct)
    {
        await _promptGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PendingApproval? first;
            lock (_lock)
            {
                first = _pending.FirstOrDefault();
            }

            if (first is null) return;

            AnsiConsole.Clear();
            var p = new Panel(new Rows(
                    new Markup("[bold yellow]新的投屏请求[/]"),
                    new Markup($"标题: [white]{Markup.Escape(first.Item.Title)}[/]"),
                    new Markup($"来源: [white]{Markup.Escape(first.Item.Source?.Display ?? "?")}[/]"),
                    new Markup($"URL:  [grey]{Markup.Escape(first.Item.OriginalUri).EllipsisIfLong(100)}[/]")))
                .Header(" 投屏审批 ").Expand();
            AnsiConsole.Write(p);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("处理方式:")
                    .AddChoices(
                        "允许（仅此一次）",
                        "允许（并记住此来源）",
                        "拒绝"));

            switch (choice)
            {
                case "允许（仅此一次）":
                    _commands.TryWrite(new CoordinatorCommand.ApproveCast(first.Id, false));
                    break;
                case "允许（并记住此来源）":
                    _commands.TryWrite(new CoordinatorCommand.ApproveCast(first.Id, true));
                    break;
                default:
                    _commands.TryWrite(new CoordinatorCommand.RejectCast(first.Id, false));
                    break;
            }

            AnsiConsole.Clear();
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private enum FocusPane
    {
        Queue,
        Devices
    }
}

internal static class MarkupExtensions
{
    public static string EllipsisIfLong(this string s, int max)
    {
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}