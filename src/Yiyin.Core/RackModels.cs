using System.Text.Json.Serialization;

namespace LightDeck.Core;

public enum RackTrackKind { Mono, Stereo, Playback, Bus, FX, Output }

public sealed class RackProject : IJsonOnDeserialized
{
    public string ImportNotes { get; set; } = "";
    public List<RackTrack> Tracks { get; set; } = [];
    public List<RackIoPort> InputPorts { get; set; } = [];
    public List<RackIoPort> OutputPorts { get; set; } = [];
    public Guid? SelectedTrackId { get; set; }
    public List<RackDisplayView> DisplayViews { get; set; } = [];
    public Guid? ActiveDisplayViewId { get; set; }
    private Guid? _playbackTrackId;
    private bool _playbackTargetSpecified;
    public Guid? PlaybackTrackId
    {
        get => _playbackTargetSpecified ? _playbackTrackId : Tracks?.Where(t => t.Kind == RackTrackKind.Playback && t.Channels == 2).Take(2).ToArray() is { Length: 1 } tracks ? tracks[0].Id : null;
        set { _playbackTargetSpecified = true; _playbackTrackId = value; }
    }
    [JsonIgnore] public List<string> CompatibilityChanges { get; } = [];
    void IJsonOnDeserialized.OnDeserialized()
    {
        EnsureDisplayViews();
        if (!_playbackTargetSpecified) { PlaybackTrackId = PlaybackTrackId; if (PlaybackTrackId is not null) CompatibilityChanges.Add("旧工程唯一播放轨已设为播放器目标。"); }
        foreach (var track in Tracks ?? [])
        {
            if (track.LegacyGainClamped) CompatibilityChanges.Add($"轨道“{track.Name}”原增益超过 +12 dB，已兼容调整到 +12 dB。");
            foreach (var send in track.Sends ?? []) if (send.LegacyGainClamped) CompatibilityChanges.Add($"轨道“{track.Name}”的发送原增益超过 +12 dB，已兼容调整到 +12 dB。");
        }
    }
    public RackTrack? FindTrack(Guid id) => Tracks.FirstOrDefault(track => track.Id == id);
    public RackProject Copy()
    {
        var copy = new RackProject { ImportNotes = ImportNotes, Tracks = Tracks.Select(track => track.Copy()).ToList(), SelectedTrackId = SelectedTrackId, PlaybackTrackId = PlaybackTrackId, InputPorts = InputPorts.Select(p => p with { }).ToList(), OutputPorts = OutputPorts.Select(p => p with { }).ToList(), DisplayViews = DisplayViews.Select(v => v.Copy()).ToList(), ActiveDisplayViewId = ActiveDisplayViewId };
        copy.CompatibilityChanges.AddRange(CompatibilityChanges); return copy;
    }
    public void Validate() => RackValidation.Validate(this);
    public void EnsureDisplayViews()
    {
        DisplayViews ??= [];
        if (DisplayViews.Count == 0) DisplayViews.Add(new() { Name = "原显示布局", TrackIds = (Tracks ?? []).Where(t => t is not null && t.Visible).Select(t => t.Id).ToList() });
        var ids = (Tracks ?? []).Where(t => t is not null).Select(t => t.Id).ToHashSet();
        foreach (var view in DisplayViews) if (view is not null) view.TrackIds = (view.TrackIds ?? []).Where(ids.Contains).Distinct().ToList();
        if (!DisplayViews.Any(v => v is not null && v.Id == ActiveDisplayViewId)) ActiveDisplayViewId = DisplayViews.FirstOrDefault(v => v is not null)?.Id;
    }
    public void CaptureActiveDisplayView()
    {
        EnsureDisplayViews();
        if (DisplayViews.FirstOrDefault(v => v.Id == ActiveDisplayViewId) is { } view) view.TrackIds = Tracks.Where(t => t.Visible).Select(t => t.Id).ToList();
    }
    public void ApplyDisplayView(Guid id)
    {
        EnsureDisplayViews();
        var view = DisplayViews.FirstOrDefault(v => v.Id == id) ?? throw new ArgumentException("显示视图已不存在。");
        var visible = view.TrackIds.ToHashSet(); foreach (var track in Tracks) track.Visible = visible.Contains(track.Id);
        ActiveDisplayViewId = id;
        if (SelectedTrackId is { } selected && !visible.Contains(selected)) SelectedTrackId = Tracks.FirstOrDefault(t => t.Visible)?.Id;
    }
    public void RemoveTrack(Guid id)
    {
        var index = Tracks.FindIndex(track => track.Id == id);
        if (index < 0) throw new ArgumentException("机架轨道已不存在。");
        if (PlaybackTrackId == id) PlaybackTrackId = null;
        Tracks.RemoveAt(index);
        foreach (var view in DisplayViews) view.TrackIds.Remove(id);
        foreach (var track in Tracks)
        {
            if (track.OutputTrackId == id) track.OutputTrackId = null;
            track.Sends.RemoveAll(send => send.TargetTrackId == id);
        }
        if (SelectedTrackId == id) SelectedTrackId = Tracks.Skip(Math.Min(index, Tracks.Count)).Concat(Tracks.Take(index).Reverse()).FirstOrDefault(t => t.Visible)?.Id;
    }
}

public sealed class RackDisplayView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新显示视图";
    public List<Guid> TrackIds { get; set; } = [];
    public RackDisplayView Copy() => new() { Id = Id, Name = Name, TrackIds = [.. TrackIds] };
}

public sealed class RackTrack : IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新轨道";
    public RackTrackKind Kind { get; set; } = RackTrackKind.Stereo;
    public string Color { get; set; } = "#347E91";
    public int Channels { get; set; } = 2;
    public double GainDb { get; set; }
    public double Pan { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    private bool? _soloSafe;
    public bool SoloSafe { get => _soloSafe ?? Kind == RackTrackKind.FX; set => _soloSafe = value; }
    public bool Monitor { get; set; }
    public bool Visible { get; set; } = true;
    public bool MainOutputEnabled { get; set; } = true;
    public double MainOutputGainDb { get; set; }
    [JsonIgnore] public bool LegacyGainClamped { get; private set; }
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (GainDb is > 12 and <= 24) { GainDb = 12; LegacyGainClamped = true; }
        if (MainOutputGainDb is > 12 and <= 24) { MainOutputGainDb = 12; LegacyGainClamped = true; }
    }
    public Guid? InputPortId { get; set; }
    public Guid? HardwareOutputPortId { get; set; }
    public Guid? OutputTrackId { get; set; }
    public List<RackSend> Sends { get; set; } = [];
    public List<RackPluginInstance> Inserts { get; set; } = [];
    public static RackTrack Create(RackTrackKind kind, string name) => new() { Kind = kind, Name = name, Channels = kind == RackTrackKind.Mono ? 1 : 2, Color = DefaultColor(kind) };
    public static string DefaultColor(RackTrackKind kind) => kind switch
    { RackTrackKind.Mono => "#447B62", RackTrackKind.Playback => "#7461A6", RackTrackKind.Bus => "#B3843E", RackTrackKind.FX => "#9A5C87", RackTrackKind.Output => "#A95555", _ => "#347E91" };
    public RackTrack Copy(bool newIds = false) => new()
    {
        Id = newIds ? Guid.NewGuid() : Id, Name = Name, Kind = Kind, Color = Color, Channels = Channels, GainDb = GainDb, Pan = Pan,
        Mute = Mute, Solo = Solo, SoloSafe = SoloSafe, Monitor = Monitor, Visible = Visible, MainOutputEnabled = MainOutputEnabled, MainOutputGainDb = MainOutputGainDb, InputPortId = InputPortId, HardwareOutputPortId = HardwareOutputPortId, OutputTrackId = OutputTrackId,
        Sends = Sends.Select(send => send with { Id = newIds ? Guid.NewGuid() : send.Id }).ToList(),
        Inserts = Inserts.Select(plugin => plugin.Copy(newIds)).ToList()
    };
}

public sealed record RackSend : IJsonOnDeserialized
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TargetTrackId { get; set; }
    public double GainDb { get; set; }
    private bool _preFader = true;
    private bool _preFaderSpecified;
    public bool PreFader { get => _preFader; set { _preFader = value; _preFaderSpecified = true; } }
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public bool LegacyGainClamped { get; private set; }
    void IJsonOnDeserialized.OnDeserialized()
    {
        // Older projects without the field used post-fader sends. Only newly
        // created sends adopt the new pre-fader default.
        if (!_preFaderSpecified) _preFader = false;
        if (GainDb is > 12 and <= 24) { GainDb = 12; LegacyGainClamped = true; }
    }
}

public sealed record RackIoPort
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新通道";
    public int Channels { get; set; } = 2;
}

public sealed record RackPluginIdentity
{
    public string Format { get; set; } = "VST3";
    public string ClassId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    [JsonIgnore] public string Key => Format.ToUpperInvariant() + ":" + ClassId;
}

public sealed class RackPluginInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public RackPluginIdentity Identity { get; set; } = new();
    // Host-defined complete processor/controller state, encoded as base64. Binary
    // locations and scan results live only in AppSettings.RackLocal.
    public string StateBase64 { get; set; } = "";
    public bool Bypass { get; set; }
    public double Wet { get; set; } = 1;
    public RackPluginInstance Copy(bool newId = false) => new()
    { Id = newId ? Guid.NewGuid() : Id, Identity = Identity with { }, StateBase64 = StateBase64, Bypass = Bypass, Wet = Wet };
}

public static class RackValidation
{
    public const int MaximumTracks = 256;
    public const int MaximumInsertsPerTrack = 32;
    public const int MaximumStateBytes = 16 * 1024 * 1024;
    public const int MaximumTotalStateBytes = 32 * 1024 * 1024;
    public static void Validate(RackProject rack)
    {
        ArgumentNullException.ThrowIfNull(rack);
        if (rack.ImportNotes is null || System.Text.Encoding.UTF8.GetByteCount(rack.ImportNotes) > 64 * 1024) throw new ArgumentException("机架导入说明不能超过 64 KiB。");
        rack.EnsureDisplayViews();
        if (rack.DisplayViews.Count > 64 || rack.DisplayViews.Any(v => v is null || v.Id == Guid.Empty || string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 80) || rack.DisplayViews.Select(v => v.Id).Distinct().Count() != rack.DisplayViews.Count) throw new ArgumentException("显示视图名称或标识无效，最多支持 64 个视图。");
        if (rack.Tracks is null || rack.Tracks.Count > MaximumTracks) throw new ArgumentException("机架轨道列表无效，最多支持 256 条。");
        if (rack.InputPorts is null || rack.OutputPorts is null || rack.InputPorts.Count > 256 || rack.OutputPorts.Count > 256) throw new ArgumentException("音频 I/O 通道列表无效。");
        var portIds = new HashSet<Guid>();
        foreach (var port in rack.InputPorts.Concat(rack.OutputPorts))
            if (port is null || port.Id == Guid.Empty || !portIds.Add(port.Id) || string.IsNullOrWhiteSpace(port.Name) || port.Name.Length > 200 || port.Channels is not (1 or 2)) throw new ArgumentException("音频 I/O 名称、标识或声道数无效。");
        var ids = new HashSet<Guid>(); var tracks = new Dictionary<Guid, RackTrack>(); long totalStateBytes = 0;
        foreach (var track in rack.Tracks)
        {
            if (track is null || track.Id == Guid.Empty || !ids.Add(track.Id)) throw new ArgumentException("机架轨道标识重复或无效。");
            if (string.IsNullOrWhiteSpace(track.Name) || track.Name.Length > 200 || !Enum.IsDefined(track.Kind)) throw new ArgumentException("机架轨道名称或种类无效。");
            if (track.Color is null || track.Color.Length != 7 || track.Color[0] != '#' || !track.Color.AsSpan(1).ToArray().All(Uri.IsHexDigit)) throw new ArgumentException("轨道颜色须为 #RRGGBB。");
            if (track.Channels is not (1 or 2) || track.Kind == RackTrackKind.Mono && track.Channels != 1 || track.Kind is RackTrackKind.Stereo or RackTrackKind.Playback && track.Channels != 2)
                throw new ArgumentException("Mono 轨为单声道，Stereo/Playback 轨为双声道；总线及输出支持 1 或 2 声道。");
            Gain(track.GainDb);
            Gain(track.MainOutputGainDb);
            if (track.InputPortId is { } inputPort && (track.Kind is not (RackTrackKind.Mono or RackTrackKind.Stereo) || !rack.InputPorts.Any(p => p.Id == inputPort && p.Channels == track.Channels))) throw new ArgumentException("轨道的输入通道不存在或单/立体声不匹配。");
            if (track.HardwareOutputPortId is { } outputPort && (track.Kind != RackTrackKind.Output || !rack.OutputPorts.Any(p => p.Id == outputPort && p.Channels == track.Channels))) throw new ArgumentException("轨道的硬件输出通道不存在或单/立体声不匹配。");
            if (!double.IsFinite(track.Pan) || track.Pan is < -1 or > 1) throw new ArgumentException("机架声像需为 -1–1。");
            if (track.Sends is null || track.Sends.Count > MaximumTracks || track.Inserts is null || track.Inserts.Count > MaximumInsertsPerTrack) throw new ArgumentException("机架发送或插件列表无效。");
            if (track.Kind == RackTrackKind.Output && (track.OutputTrackId is not null || track.Sends.Count != 0)) throw new ArgumentException("输出轨不能回送到其他机架轨道。");
            foreach (var send in track.Sends)
            {
                if (send is null || send.Id == Guid.Empty || !ids.Add(send.Id)) throw new ArgumentException("机架发送标识重复或无效。");
                Gain(send.GainDb);
            }
            foreach (var plugin in track.Inserts)
            {
                if (plugin is null || plugin.Id == Guid.Empty || !ids.Add(plugin.Id)) throw new ArgumentException("机架插件标识重复或无效。");
                ValidateIdentity(plugin.Identity);
                if (!double.IsFinite(plugin.Wet) || plugin.Wet is < 0 or > 1) throw new ArgumentException("插件干湿比需为 0–1。");
                if (plugin.StateBase64 is null || plugin.StateBase64.Length > ((MaximumStateBytes + 2L) / 3) * 4 || !IsBase64(plugin.StateBase64)) throw new ArgumentException("插件状态无效或超过 16 MiB。");
                totalStateBytes += plugin.StateBase64.Length / 4L * 3 - (plugin.StateBase64.EndsWith("==", StringComparison.Ordinal) ? 2 : plugin.StateBase64.EndsWith('=') ? 1 : 0);
                if (totalStateBytes > MaximumTotalStateBytes) throw new ArgumentException("工程插件状态合计超过 32 MiB。");
            }
            tracks.Add(track.Id, track);
        }
        if (rack.PlaybackTrackId is { } playback && (!tracks.TryGetValue(playback, out var player) || player.Kind != RackTrackKind.Playback || player.Channels != 2)) throw new ArgumentException("播放器目标必须是本工程的立体声播放轨。");
        var edges = tracks.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var track in rack.Tracks)
        {
            void Add(Guid target)
            {
                if (!tracks.TryGetValue(target, out var destination) || destination.Kind is not (RackTrackKind.Bus or RackTrackKind.FX or RackTrackKind.Output))
                    throw new ArgumentException("机架路由目标须为当前工程中的 Bus、FX 或 Output 轨。");
                edges[track.Id].Add(target);
            }
            if (track.OutputTrackId is { } output) Add(output);
            // Disabled sends still belong to the graph: enabling one must never
            // turn a previously accepted saved configuration into feedback.
            foreach (var send in track.Sends) Add(send.TargetTrackId);
        }
        var visiting = new HashSet<Guid>(); var visited = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ArgumentException("机架路由不能形成反馈回路。");
            foreach (var target in edges[id]) Visit(target);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in tracks.Keys) Visit(id);
    }
    public static void ValidateIdentity(RackPluginIdentity identity)
    {
        if (identity is null || identity.Format is not ("VST3" or "VST2") || string.IsNullOrWhiteSpace(identity.ClassId) || identity.ClassId.Length > 256 || identity.ClassId.Any(char.IsControl) || identity.ClassId.Contains('/') || identity.ClassId.Contains('\\') || Path.IsPathFullyQualified(identity.ClassId))
            throw new ArgumentException("插件身份须包含 VST3/VST2 类别标识，不得包含本机插件路径。");
        if (identity.Name is null || identity.Name.Length > 256 || identity.Vendor is null || identity.Vendor.Length > 256 || identity.Version is null || identity.Version.Length > 128) throw new ArgumentException("插件描述无效。");
    }
    private static void Gain(double gain) { if (!double.IsFinite(gain) || gain is < -90 or > 12) throw new ArgumentException("机架增益需为 -90–12 dB。"); }
    private static bool IsBase64(string value)
    {
        if (value.Length == 0) return true;
        if (value.Length % 4 != 0) return false;
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        for (var i = 0; i < value.Length - padding; i++) if (!(char.IsAsciiLetterOrDigit(value[i]) || value[i] is '+' or '/')) return false;
        for (var i = value.Length - padding; i < value.Length; i++) if (value[i] != '=') return false;
        return value.Length / 4L * 3 - padding <= MaximumStateBytes;
    }
}
