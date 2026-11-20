using System.ComponentModel;
using System.Windows;
using LightDeck.Core;

namespace LightDeck.App;

internal sealed partial class RackDeviceDialog
{
    private Task _previewWork = Task.CompletedTask;
    private bool _previewTouched, _allowClose, _closingDraft;

    private List<RackHardwarePort> AllOutputTargets(Guid id)
    {
        var targets = new List<RackHardwarePort>();
        if (_local.OutputPorts.TryGetValue(id, out var first)) targets.Add(first);
        if (_local.OutputMirrors.TryGetValue(id, out var mirrors)) targets.AddRange(mirrors);
        return targets;
    }
    private void SetOutputTargets(Guid id, List<RackHardwarePort> targets)
    {
        if (targets.Count == 0) _local.OutputPorts.Remove(id); else _local.OutputPorts[id] = targets[0];
        if (targets.Count < 2) _local.OutputMirrors.Remove(id); else _local.OutputMirrors[id] = targets.Skip(1).ToList();
    }

    private void SelectMultiCell(RackIoPort port, RackUiDevice device, int channel)
    {
        var targets = _input ? _local.InputPorts.TryGetValue(port.Id, out var old) && old.DeviceId == device.Id ? new List<RackHardwarePort> { old } : [] : AllOutputTargets(port.Id);
        var map = targets.FirstOrDefault(m => m.DeviceId == device.Id && m.Channels.Contains(channel));
        if (map is not null)
        {
            var remaining = ExpandChannels(map.Channels, port.Channels);
            remaining[Array.IndexOf(remaining, channel)] = -1;
            if (remaining.All(c => c < 0)) targets.Remove(map); else map.Channels = remaining;
        }
        else
        {
            var side = port.Channels == 1 ? 0 : channel % 2;
            map = _input ? targets.FirstOrDefault() : port.Channels == 2 ? targets.FirstOrDefault(m => m.DeviceId == device.Id && m.Channels.Any(c => c >= 0 && c / 2 == channel / 2)) : null;
            if (map is null) { map = new() { DeviceId = device.Id, DeviceName = device.Name }; targets.Add(map); }
            var mapped = ExpandChannels(map.Channels, port.Channels);
            mapped[side] = channel; map.Channels = mapped;
        }
        if (!_input) SetOutputTargets(port.Id, targets);
        else if (targets.Count == 0) _local.InputPorts.Remove(port.Id); else _local.InputPorts[port.Id] = targets[0];
    }
    private static int[] ExpandChannels(int[] channels, int width)
    {
        var result = Enumerable.Repeat(-1, width).ToArray();
        Array.Copy(channels, result, Math.Min(channels.Length, width)); return result;
    }

    private void PrepareMultiSettings(RackLocalSettings settings)
    {
        if (_clock.SelectedItem is not RackUiDevice { Available: true } clock || !_selectedIds.Contains(clock.Id))
            throw new ArgumentException("请选择一台已勾选的可用主时钟输出设备。");
        if (_mode == "hybrid" && (clock.Api != "asio" || clock.ProbeRequired))
            throw new ArgumentException("请勾选并探测一台 ASIO 专业声卡作为主时钟。");
        if (_rate.SelectedItem is not int rate || _buffer.SelectedItem is not int buffer)
            throw new ArgumentException("请选择主时钟支持的采样率和缓冲帧数。");
        settings.SelectedEndpointIds = _selectedIds.Where(RelevantId).Order(StringComparer.Ordinal).ToList();
        settings.DeviceApi = clock.Api; settings.OutputDeviceId = clock.Id; settings.OutputDeviceName = clock.Name;
        settings.SampleRate = rate; settings.BufferSize = buffer;
        var selected = settings.SelectedEndpointIds.ToHashSet();
        foreach (var input in new[] { true, false })
        {
            foreach (var port in input ? _project.InputPorts : _project.OutputPorts)
            {
                port.Name = port.Name.Trim();
                var targets = input ? settings.InputPorts.TryGetValue(port.Id, out var m) ? new[] { m } : [] : settings.OutputTargets(port.Id).ToArray();
                foreach (var map in targets)
                {
                    if (!selected.Contains(map.DeviceId))
                        throw new ArgumentException($"“{port.Name}”仍绑定未选设备“{map.DeviceName}”，请重新勾选该设备或更改映射。");
                    var d = ActiveDevices.FirstOrDefault(d => d.Available && d.Id == map.DeviceId);
                    if (map.Channels.Length != port.Channels || d is not null && map.Channels.Any(c => c >= (input ? d.InputChannels : d.OutputChannels)))
                        throw new ArgumentException($"“{port.Name}”的硬件声道已变化，请重新映射。");
                }
            }
        }
        var inputMap = settings.InputPorts.Values.FirstOrDefault(m => selected.Contains(m.DeviceId));
        settings.InputDeviceId = inputMap?.DeviceId ?? ""; settings.InputDeviceName = inputMap?.DeviceName ?? "";
    }

    private bool MatchesRunningStreams()
    {
        var saved = _host.Local;
        return Multi && _host.Status.IsRunning && !_host.Status.IsBusy && _guard.IsCurrent && _mode == saved.IoMode &&
            (_clock.SelectedItem as RackUiDevice)?.Id == saved.OutputDeviceId &&
            Equals(_rate.SelectedItem, saved.SampleRate) && Equals(_buffer.SelectedItem, saved.BufferSize) &&
            _selectedIds.Where(RelevantId).ToHashSet().SetEquals(saved.SelectedEndpointIds);
    }

    private bool _reconcileQueued;
    private void ReconcileStreamSelection()
    {
        if (_reconcileQueued || !_previewTouched && _previewWork.IsCompleted) return;
        _reconcileQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _reconcileQueued = false;
            if (_closed || _closingDraft || _busy || MatchesRunningStreams()) return;
            _previewWork = RestoreAfterAsync(_previewWork);
        });
    }

    private async Task RestoreAfterAsync(Task previous)
    {
        await previous;
        var error = await RestorePreviewAsync();
        if (error.Length > 0) Invalid(error.Trim());
    }

    private void QueuePreview()
    {
        if (_busy || _closingDraft || _closed || !MatchesRunningStreams()) return;
        var settings = RackWorkspaceControl.CopyLocal(_local);
        settings.IoMode = _host.Local.IoMode;
        var project = _project.Copy();
        _previewWork = PreviewAfterAsync(_previewWork, project, settings);
    }
    private async Task PreviewAfterAsync(Task previous, RackProject project, RackLocalSettings settings)
    {
        await previous;
        if (_closingDraft || _closed || !MatchesRunningStreams()) return;
        try
        {
            _previewTouched = true;
            await _host.PreviewHardwareRoutesAsync(project, settings);
        }
        catch (Exception ex)
        {
            var error = await RestorePreviewAsync();
            Invalid("实时通道映射未应用：" + ex.Message + error);
        }
    }
    private async Task<string> RestorePreviewAsync()
    {
        if (!_previewTouched) return "";
        try
        {
            if (_guard.IsCurrent && _host.Status.IsRunning)
                await _host.PreviewHardwareRoutesAsync(_host.Project, _host.Local);
            _previewTouched = false; return "";
        }
        catch (Exception ex) { return " 恢复已保存映射失败：" + ex.Message; }
    }
    private async void OnDialogClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_busy || _closingDraft) { e.Cancel = true; return; }
        if (!_previewTouched && _previewWork.IsCompleted) return;
        e.Cancel = true; _closingDraft = true; _busy = true; UpdateActionAvailability();
        // Exit the current WPF Closing event before issuing the final Close.
        await Task.Yield();
        await _previewWork;
        var error = await RestorePreviewAsync();
        _busy = _closingDraft = false; UpdateActionAvailability();
        if (error.Length > 0) { Invalid(error.Trim()); return; }
        _allowClose = true; Close();
    }
}

