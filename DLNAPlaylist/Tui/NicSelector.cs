using DLNAPlaylist.Util;

using Spectre.Console;

namespace DLNAPlaylist.Tui;

/// <summary>
///     启动时的网卡选择器：在 TUI Live 启动之前用 Spectre.Console 直接弹一个选择菜单。
///     多于一张候选时才询问；一张直接返回；零张返回 null（外层报错）。
/// </summary>
public static class NicSelector
{
    public static NetworkInterfaces.Candidate? Select(bool forcePrompt)
    {
        var candidates = NetworkInterfaces.EnumerateCandidates();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 && !forcePrompt) return candidates[0];

        // 视觉上把虚拟/VPN 类标红，让用户一眼看出推荐项
        var labels = candidates.Select(BuildLabel).ToArray();
        var labelToCandidate = candidates
            .Select((c, i) => (Label: labels[i], C: c))
            .ToDictionary(x => x.Label, x => x.C);

        AnsiConsole.Write(new Rule("[bold yellow]选择要绑定的网卡[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]DLNA 设备发现和媒体投送都会通过这张网卡。如果开了 VPN 或虚拟机，请避开它们。[/]");
        AnsiConsole.MarkupLine("[grey]推荐项排在第一位（评分最高，已自动避开 VPN/虚拟网卡）。按 Enter 选定。[/]");
        AnsiConsole.WriteLine();

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("网卡：")
                .PageSize(Math.Max(3, Math.Min(candidates.Count + 1, 10)))
                .AddChoices(labels));

        AnsiConsole.WriteLine();
        return labelToCandidate[picked];
    }

    private static string BuildLabel(NetworkInterfaces.Candidate c)
    {
        // 注意：SelectionPrompt 用字符串做 key，必须保证唯一 —— IP 已天然唯一
        var virtualTag = c.LooksVirtual ? "[red][[VPN/虚拟]][/] " : "";
        var gwTag = c.HasGateway ? "[green]GW[/]" : "[grey]no-GW[/]";
        var name = Markup.Escape(c.Ni.Name);
        var desc = Markup.Escape(TrimDesc(c.Ni.Description));
        return $"{virtualTag}[bold]{c.Address,-15}[/]  {gwTag}  {name}  [grey]- {desc}[/]";
    }

    private static string TrimDesc(string s)
    {
        const int max = 60;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}