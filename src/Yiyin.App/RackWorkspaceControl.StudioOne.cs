using System.IO;
using System.Windows;
using LightDeck.Core;
using Microsoft.Win32;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private async Task ImportStudioOneAsync()
    {
        if (!_host.IsEditing || _host.RackLibrary is not { } library) return;
        var picker = new OpenFileDialog { Title = "选择 Studio One 工程", Filter = "Studio One 工程|*.song", CheckFileExists = true };
        if (picker.ShowDialog(_owner) != true) return;
        _error.Text = "正在读取工程；当前机架继续运行…";
        var result = await Task.Run(() => StudioOneSongImporter.Import(picker.FileName));
        if (_disposed || !_host.IsEditing) return;
        _error.Text = "";
        var dialog = new StudioOneImportDialog(_owner, _theme, result, _host.Local, library);
        dialog.ShowDialog();
        if (dialog.Switched) _error.Text = "已切换到导入机架，当前停止。请在“音频 I/O”核对声卡通道，再开启机架。原机架可从“更多 → 机架管理”切回。";
    }

    private void ShowRackLibrary()
    {
        if (!_host.IsEditing || _busy || _host.RackLibrary is not { } library) return;
        new RackLibraryDialog(_owner, _theme, library).ShowDialog();
    }
}

internal static class StudioOneImportPresentation
{
    internal static bool Available(RackPluginInstance plugin, RackLocalSettings local) => local.PluginCatalog.Any(item => item.Available &&
        item.Identity.Format.Equals(plugin.Identity.Format, StringComparison.OrdinalIgnoreCase) &&
        item.Identity.ClassId.Equals(plugin.Identity.ClassId, StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path));

    internal static string BuildReport(StudioOneImportResult result, RackLocalSettings local)
    {
        var rack = result.Project; var lines = new List<string>
        {
            $"Studio One 导入：{result.Title}", $"来源：{result.Generator}；采样率：{result.SourceSampleRate} Hz", $"源文件 SHA256：{result.SourceSha256}",
            $"轨道 {rack.Tracks.Count} · 插件 {rack.Tracks.Sum(t => t.Inserts.Count)} · 效果/附加发送 {rack.Tracks.Sum(t => t.Sends.Count)}", "",
            "I/O 原始映射（仅供核对，尚未绑定本机声卡）"
        };
        foreach (var port in result.IoHints) lines.Add($"{(port.IsInput ? "输入" : "输出")} · {port.PortName} · {port.Channels} 声道 → {port.DeviceName} · 通道 {string.Join(" / ", port.ChannelIndices.Select(i => i + 1))} · {string.Join(" / ", port.ChannelNames)}");
        lines.Add(""); lines.Add("轨道 / 插件 / 路由");
        foreach (var track in rack.Tracks)
        {
            lines.Add($"{track.Name} [{track.Kind}] · {track.GainDb:0.0} dB · 声像 {track.Pan:0.00} · {(track.Visible ? "显示" : "隐藏")} · {(track.Mute ? "关闭" : "开启")}");
            foreach (var plugin in track.Inserts) lines.Add($"  插入：{plugin.Identity.Name} · {plugin.Identity.Format} · {(Available(plugin, local) ? "本机已扫描匹配" : "本机不可用 / 未扫描")} · {(plugin.Bypass ? "旁通" : "启用")}");
            lines.Add("  主去向：" + (rack.FindTrack(track.OutputTrackId ?? Guid.Empty)?.Name ?? rack.OutputPorts.FirstOrDefault(p => p.Id == track.HardwareOutputPortId)?.Name ?? "未分配"));
            foreach (var send in track.Sends) lines.Add($"  发送：{rack.FindTrack(send.TargetTrackId)?.Name} · {(send.PreFader ? "推子前" : "推子后")} · {send.GainDb:0.0} dB · {(send.Enabled ? "开启" : "关闭")}");
        }
        lines.Add(""); lines.Add("转换说明 / 需要处理的差异");
        lines.AddRange(result.Issues.Select(i => $"[{i.Severity}] {i.Message}"));
        lines.Add("存在缺失插件时机架不能启动（旁通也不例外）；请安装并扫描匹配插件，或替换/移除红色插槽。Studio One 自带插件不能直接在易控运行。");
        lines.Add("本机匹配状态只反映导入时扫描缓存与路径；实际插件授权和参数恢复结果仍以加载为准。未导入时间线播放、自动化或外部音频素材。");
        return string.Join(Environment.NewLine, lines);
    }
}
