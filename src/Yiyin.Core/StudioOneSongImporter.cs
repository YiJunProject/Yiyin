using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace LightDeck.Core;

/// <summary>Reads a Studio One mixer snapshot without opening devices, plug-ins or external files.</summary>
public static class StudioOneSongImporter
{
    private const int MaximumEntries = 20000;
    private const int MaximumXmlBytes = 16 * 1024 * 1024;
    private const int MaximumPresetBytes = 12 * 1024 * 1024;

    public static StudioOneImportResult Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var reader = new Reader(archive);
        var result = reader.Read(Path.GetFileNameWithoutExtension(path));
        result.SourceSha256 = hash;
        return result;
    }

    private sealed class Reader
    {
        private readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly StudioOneImportResult result = new();
        private readonly Dictionary<string, RackTrack> tracks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RackIoPort> inputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RackIoPort> outputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (bool Visible, int Order)> layout = new(StringComparer.OrdinalIgnoreCase);
        private long totalRead;

        public Reader(ZipArchive archive)
        {
            if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("Studio One 工程条目过多，无法安全读取。");
            foreach (var entry in archive.Entries)
            {
                var name = SafePath(entry.FullName);
                if (name.EndsWith('/')) continue;
                if (!entries.TryAdd(name, entry)) throw new InvalidDataException("Studio One 工程包含重复的文件条目。");
            }
        }

        public StudioOneImportResult Read(string fallbackTitle)
        {
            var mixer = Xml("Devices/audiomixer.xml", true)!;
            if (mixer.DocumentElement?.Name != "AudioMixer") throw new InvalidDataException("文件内没有可识别的 Studio One 混音器。");
            var meta = Xml("metainfo.xml");
            result.Title = Metadata(meta, "Document:Title") is { Length: > 0 } title ? title : fallbackTitle;
            result.Generator = Metadata(meta, "Document:Generator");
            result.SourceSampleRate = int.TryParse(Metadata(meta, "Media:SampleRate"), out var rate) ? rate : 0;
            if (!(result.Generator.StartsWith("Studio One/5.", StringComparison.OrdinalIgnoreCase)
                || result.Generator.StartsWith("Studio One/6.", StringComparison.OrdinalIgnoreCase)
                || result.Generator.StartsWith("Studio One/7.", StringComparison.OrdinalIgnoreCase)))
                Warn("generator-unverified", "来源版本未识别为 Studio One 5、6 或 7；将按已识别结构读取，请逐项检查预览。");
            ReadLayout(Xml("Devices/mixerconsole.xml"));
            var channels = Child(Child(mixer.DocumentElement, "channels"), "ChannelGroup", false)
                .SelectMany(Elements).ToArray();
            if (channels.Length == 0) throw new InvalidDataException("Studio One 工程没有混音通道。");
            foreach (var channel in channels.Where(c => c.Name is "AudioInputChannel" or "AudioOutputChannel")) ReadPort(channel);
            foreach (var channel in channels.Where(c => c.Name != "AudioInputChannel")) ReadTrack(channel);
            if (result.Project.Tracks.Count > RackValidation.MaximumTracks) throw new InvalidDataException("工程超过易控支持的 256 条机架轨道。");
            foreach (var channel in channels.Where(c => c.Name != "AudioInputChannel")) ReadConnections(channel);
            ReadIoHints(Xml("Devices/audioiomanager.xml"));
            ReadSongNotice(Xml("Song/song.xml"));
            result.Project.Tracks = result.Project.Tracks.OrderBy(t => layout.TryGetValue(SourceId(t.Id), out var value) ? value.Order : int.MaxValue).ToList();
            result.Project.SelectedTrackId = result.Project.Tracks.FirstOrDefault(t => t.Visible)?.Id ?? result.Project.Tracks.FirstOrDefault()?.Id;
            result.Project.PlaybackTrackId = null;
            result.Project.DisplayViews.Add(new() { Name = "Studio One 显示布局", TrackIds = result.Project.Tracks.Where(t => t.Visible).Select(t => t.Id).ToList() });
            result.Project.EnsureDisplayViews();
            try { result.Project.Validate(); }
            catch (ArgumentException ex) { Error("rack-validation", "该工程不能直接切换为机架：" + ex.Message); }
            if (result.Project.Tracks.Count == 0) Error("empty-rack", "未读取到可导入的混音轨道。");
            result.Issues.Add(new(StudioOneImportSeverity.Information, "snapshot-only", "导入混音器当前静态参数；不导入时间线音频、乐器演奏、自动化曲线、Solo 和录制流程。物理声卡映射需在切换前确认。"));
            return result;
        }

        private string SourceId(Guid trackId) => result.TrackMappings.First(m => m.TrackId == trackId).SourceId;

        private void ReadLayout(XmlDocument? document)
        {
            if (document is null) return;
            foreach (var section in Child(Child(document.DocumentElement, "channelSettings"), "Section", false))
            {
                var source = CanonicalId(A(section, "path"));
                var attributes = Elements(section).FirstOrDefault();
                if (source.Length == 0 || attributes is null) continue;
                var order = int.TryParse(A(attributes, "order"), out var parsed) && parsed >= 0 ? parsed : int.MaxValue;
                layout[source] = (A(attributes, "visible") != "0", order);
            }
        }

        private void ReadPort(XmlElement channel)
        {
            var input = channel.Name == "AudioInputChannel";
            var id = RequiredId(channel);
            var channels = Channels(channel);
            var port = new RackIoPort { Name = Name(channel), Channels = channels };
            var map = input ? inputs : outputs;
            if (!map.TryAdd(id, port)) throw new InvalidDataException("Studio One 音频 I/O 标识重复。");
            (input ? result.Project.InputPorts : result.Project.OutputPorts).Add(port);
            if (input && (Math.Abs(Number(channel, "gain", 1) - 1) > 0.00001 || Math.Abs(Number(channel, "pan", .5) - .5) > .00001
                || PluginSlots(Child(channel, "Inserts")).Any()))
                Error("input-port-processing", $"输入 I/O“{port.Name}”自身含增益、声像或插入处理；易控的逻辑输入尚不能等价承载该级处理。请在 Studio One 移到普通轨道后重新导入。");
        }

        private void ReadTrack(XmlElement channel)
        {
            var kind = channel.Name switch
            {
                "AudioTrackChannel" => Channels(channel) == 1 ? RackTrackKind.Mono : RackTrackKind.Stereo,
                "AudioEffectChannel" => RackTrackKind.FX,
                "AudioGroupChannel" => RackTrackKind.Bus,
                "AudioOutputChannel" => RackTrackKind.Output,
                _ => (RackTrackKind?)null
            };
            if (kind is null) { Warn("unsupported-channel", $"未导入通道“{Name(channel)}”（{channel.Name}）；乐器、监听及特殊通道不等同于音频机架轨道。"); return; }
            var source = RequiredId(channel);
            var track = RackTrack.Create(kind.Value, Name(channel));
            track.Channels = Channels(channel);
            track.GainDb = Gain(Number(channel, "gain", 1), track.Name + "推子", track.Id);
            track.Pan = Pan(Number(channel, "pan", .5), track.Name, track.Id);
            track.Mute = Flag(channel, "mute");
            track.Color = Color(A(channel, "color"), track.Color);
            track.Visible = !layout.TryGetValue(source, out var state) || state.Visible;
            if (Flag(channel, "solo")) Warn("solo-not-imported", $"“{track.Name}”的 Solo 未导入，请检查整条信号链。", track.Id);
            if (!tracks.TryAdd(source, track)) throw new InvalidDataException("Studio One 混音轨道标识重复。");
            result.Project.Tracks.Add(track);
            result.TrackMappings.Add(new(source, track.Id, channel.Name, track.Name));
            if (track.Kind == RackTrackKind.Output) track.HardwareOutputPortId = outputs[source].Id;
            ReadPlugins(channel, track);
            if (PluginSlots(Child(channel, "PostFaderInserts")).Any()) Error("post-fader-inserts", $"“{track.Name}”含推子后插入；易控当前插入链位于推子前，不能无损转换。", track.Id);
            var inputFx = Child(channel, "InputFX");
            if (inputFx is not null && (Number(inputFx, "gain", 0) != 0 || Flag(inputFx, "invertPhaseL") || Flag(inputFx, "invertPhaseR")))
                Warn("input-processing", $"“{track.Name}”的输入 Trim／相位反转尚未导入。", track.Id);
            var automation = Child(channel, "Automation");
            if (automation is not null && Number(automation, "mode", 0) != 0) Warn("automation-active", $"“{track.Name}”启用了自动化；仅导入保存时的静态参数。", track.Id);
        }

        private void ReadConnections(XmlElement channel)
        {
            if (!tracks.TryGetValue(RequiredId(channel), out var track)) return;
            var destination = Child(channel, "destination");
            if (destination is not null)
            {
                if (ResolveTarget(destination, track, "主去向") is { } target) track.OutputTrackId = target.Id;
            }
            if (track.Kind is RackTrackKind.Mono or RackTrackKind.Stereo)
            {
                var source = Child(Child(channel, "RecordUnit"), "recordPort");
                var sourceId = ConnectionId(source);
                if (inputs.TryGetValue(sourceId, out var port) && port.Channels == track.Channels)
                {
                    track.InputPortId = port.Id;
                    track.Monitor = true;
                    if (Child(source, "data") is { } data && !Flag(data, "monitorActive"))
                    {
                        track.Mute = true;
                        track.Monitor = false;
                        Warn("monitor-disabled", $"“{track.Name}”原输入监听关闭，导入后保持轨道关闭；确认输入后可手动开启。", track.Id);
                    }
                }
                else Warn("input-unassigned", $"“{track.Name}”没有可匹配的硬件输入，保留为未分配输入轨；时间线音频不会自动成为易控播放器。", track.Id);
            }
            foreach (var slot in Elements(Child(channel, "Sends")).Where(e => Child(e, "destination") is not null))
            {
                var connection = Child(slot, "destination")!;
                var target = ResolveTarget(connection, track, "发送");
                if (target is null) continue;
                var send = new RackSend { TargetTrackId = target.Id, GainDb = Gain(Number(slot, "level", 1), $"{track.Name} → {target.Name}", track.Id), PreFader = Flag(slot, "prefader"), Enabled = !Flag(slot, "bypass") };
                track.Sends.Add(send);
                if (Math.Abs(Number(slot, "pan", .5) - .5) > .00001)
                    Warn("send-pan", $"“{track.Name} → {target.Name}”有独立发送声像；易控目前发送沿用轨道声像，此独立值未导入。", track.Id);
            }
        }

        private RackTrack? ResolveTarget(XmlElement connection, RackTrack source, string route)
        {
            var objectId = A(connection, "objectID");
            var targetId = ConnectionId(connection);
            // Side-chain ports belong to a plugin, never to the audio input of
            // its containing track. Routing to that track would change the mix.
            if (objectId.Contains("/AudioInput", StringComparison.OrdinalIgnoreCase))
            {
                Warn("sidechain", $"“{source.Name}”的{route}“{A(connection, "friendlyName") }”是插件外部侧链；该连接未导入，未改接到普通轨道。", source.Id);
                return null;
            }
            if (!tracks.TryGetValue(targetId, out var target) || target.Kind is not (RackTrackKind.Bus or RackTrackKind.FX or RackTrackKind.Output))
            {
                Warn("unresolved-route", $"“{source.Name}”的{route}“{A(connection, "friendlyName") }”目标未识别，该连接未导入。", source.Id);
                return null;
            }
            return target;
        }

        private void ReadPlugins(XmlElement channel, RackTrack track)
        {
            var inserts = Child(channel, "Inserts");
            var slots = PluginSlots(inserts).ToArray();
            if (slots.Length > RackValidation.MaximumInsertsPerTrack) throw new InvalidDataException($"“{track.Name}”超过 32 个插入插件。");
            foreach (var slot in slots)
            {
                var ghost = Child(slot, "ghostData");
                var info = Child(ghost, "classInfo");
                var name = A(info, "name");
                if (name.Length == 0) name = A(Child(slot, "deviceData"), "name");
                if (name.Length == 0) name = A(slot, "name");
                var presetPath = A(Child(slot, "presetPath"), "text");
                var format = A(ghost, "presetType").ToLowerInvariant();
                if (format.Length == 0) format = Path.GetExtension(presetPath).TrimStart('.').ToLowerInvariant();
                var classId = A(Child(slot, "deviceClassID"), "uid");
                var plugin = new RackPluginInstance
                {
                    Identity = new() { Name = Limit(name, 256), Format = format is "fxb" or "fxp" ? "VST2" : "VST3", ClassId = "studio-one-unavailable:" + CanonicalId(classId), Vendor = Limit(A(info, "vendor"), 256) },
                    Bypass = Flag(slot, "bypass") || Flag(inserts, "bypass"),
                    Wet = 1
                };
                var imported = false;
                byte[]? bytes = null;
                try
                {
                    if (presetPath.Length == 0) throw new InvalidDataException("工程没有保存此插件的预设文件。");
                    bytes = Bytes(presetPath, MaximumPresetBytes, true)!;
                    if (format == "vstpreset")
                    {
                        var state = ReadVst3(bytes);
                        plugin.Identity.ClassId = state.ClassId;
                        plugin.StateBase64 = state.State;
                        imported = true;
                        var xmlId = CanonicalId(classId);
                        if (xmlId.Length != 0 && !state.ClassId.Equals(xmlId, StringComparison.OrdinalIgnoreCase))
                            Warn("preset-class-id", $"“{track.Name} / {name}”工程描述与预设的插件标识不同，采用预设中的完整标识，禁止按名称替换。", track.Id, plugin.Id);
                    }
                    else if (format is "fxb" or "fxp")
                    {
                        var state = ReadVst2(bytes);
                        plugin.Identity.ClassId = state.ClassId;
                        plugin.StateBase64 = state.State;
                        imported = true;
                        if (state.PartialBank) Warn("vst2-program-only", $"“{track.Name} / {name}”参数型预设库仅导入当前程序；其他程序未导入。", track.Id, plugin.Id);
                    }
                    else
                    {
                        plugin.Identity.ClassId = "studio-one-native:" + (CanonicalId(classId) is { Length: > 0 } nativeId ? nativeId : plugin.Id.ToString("N"));
                        plugin.Identity.Vendor = "PreSonus / Studio One";
                        Warn("native-plugin", $"“{track.Name} / {name}”是 Studio One 自带或专用插件，易控无法运行；保留原预设和插槽，请手动替换。", track.Id, plugin.Id);
                    }
                }
                catch (InvalidDataException ex)
                {
                    Warn("plugin-state-unavailable", $"“{track.Name} / {name}”未恢复：{ex.Message} 保留不可用插槽，不使用默认参数冒充原状态。", track.Id, plugin.Id);
                    plugin.Identity.ClassId = "studio-one-unavailable:" + plugin.Id.ToString("N");
                }
                if (!imported)
                    plugin.StateBase64 = Encode(new { studioOnePresetType = format, studioOnePreset = bytes is null ? "" : Convert.ToBase64String(bytes) });
                if (Number(slot, "mix", 1) != 1 || Number(slot, "wet", 1) != 1) Warn("plugin-mix", $"“{track.Name} / {name}”的宿主混合比例未识别；插件内部干湿参数仍保留在状态中。", track.Id, plugin.Id);
                track.Inserts.Add(plugin);
                result.PluginMappings.Add(new(CanonicalId(A(Child(slot, "uniqueID"), "uid")), track.Id, plugin.Id, presetPath, format, imported));
            }
            if (Elements(inserts).Any(e => e.Name.Contains("Splitter", StringComparison.OrdinalIgnoreCase) || e.GetAttribute("name").Contains("Splitter", StringComparison.OrdinalIgnoreCase)))
                Error("splitter", $"“{track.Name}”含并联或分频 Splitter，不能当作串联插入导入。", track.Id);
        }

        private void ReadIoHints(XmlDocument? document)
        {
            foreach (var device in document?.GetElementsByTagName("AudioIOAssignment").OfType<XmlElement>() ?? [])
            {
                foreach (var input in new[] { true, false })
                {
                    var ports = input ? inputs : outputs;
                    foreach (var assignment in Elements(Child(device, input ? "inputs" : "outputs")))
                    {
                        if (!ports.TryGetValue(CanonicalId(A(assignment, "name")), out var port)) continue;
                        var associations = Elements(assignment).Where(e => e.Name == "Association").ToDictionary(e => A(e, "key"), e => A(e, "value"), StringComparer.Ordinal);
                        var keys = port.Channels == 1 ? new[] { "M" } : new[] { "L", "R" };
                        var names = new List<string>(); var indices = new List<int>();
                        foreach (var key in keys)
                        {
                            if (!associations.TryGetValue(key, out var value)) break;
                            var name = value[(value.LastIndexOf('/') + 1)..];
                            var digits = new string(name.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
                            if (!int.TryParse(digits, out var index) || index is < 0 or > 4095) break;
                            names.Add(name); indices.Add(index);
                        }
                        if (indices.Count == port.Channels)
                            result.IoHints.Add(new(port.Id, input, port.Name, port.Channels, A(device, "deviceName"), A(device, "deviceType"), indices, names));
                        else Warn("io-map-unresolved", $"I/O“{port.Name}”在“{A(device, "deviceName") }”上的硬件通道未能完整识别，请手动映射。");
                    }
                }
            }
        }

        private void ReadSongNotice(XmlDocument? song)
        {
            if (song is null) return;
            var events = song.GetElementsByTagName("AudioEvent").Count;
            if (events > 0) Warn("timeline-media", $"工程含 {events} 个时间线音频片段；本次仅迁移机架，片段和播放位置未导入。输入轨必须另行确认实时来源。");
        }

        private byte[]? Bytes(string path, int maximum, bool required = false)
        {
            path = SafePath(path);
            if (!entries.TryGetValue(path, out var entry))
            {
                if (required) throw new InvalidDataException("缺少工程内文件：" + path);
                return null;
            }
            if (entry.Length > maximum || entry.Length < 0) throw new InvalidDataException("工程内文件超出读取大小限制：" + path);
            totalRead += entry.Length;
            if (totalRead > 96L * 1024 * 1024) throw new InvalidDataException("机架元数据及插件状态超过读取预算。");
            using var stream = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            var buffer = new byte[16384]; int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("解压后的工程内文件超过读取限制。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }

        private XmlDocument? Xml(string path, bool required = false)
        {
            var bytes = Bytes(path, MaximumXmlBytes, required);
            if (bytes is null) return null;
            try
            {
                using var stream = new MemoryStream(bytes, false);
                // Studio One uses literal x:id attributes without declaring x.
                // Namespace-disabled parsing preserves them without rewriting XML.
                using var reader = new XmlTextReader(stream) { Namespaces = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                var document = new XmlDocument { XmlResolver = null };
                document.Load(reader);
                return document;
            }
            catch (XmlException ex) { throw new InvalidDataException("Studio One XML 无效：" + path, ex); }
        }

        private double Gain(double linear, string name, Guid trackId)
        {
            if (!double.IsFinite(linear) || linear < 0) throw new InvalidDataException("工程增益值无效：" + name);
            var db = linear == 0 ? -90 : 20 * Math.Log10(linear);
            if (db > 12 || db < -90 && linear > 0) Warn("gain-clamped", $"“{name}”超出易控 -90～+12 dB 范围，导入时限制到边界。", trackId);
            return Math.Clamp(db, -90, 12);
        }
        private double Pan(double value, string name, Guid trackId)
        {
            if (!double.IsFinite(value) || value < 0 || value > 1) throw new InvalidDataException("工程声像值无效：" + name);
            return value * 2 - 1;
        }
        private int Channels(XmlElement channel)
        {
            var type = A(Child(channel, "speakerType"), "type");
            if (type == "Mono") return 1;
            if (type is "Stereo" or "") return 2;
            throw new InvalidDataException($"“{Name(channel)}”使用 {type} 多声道布局，当前机架只支持单声道和立体声。");
        }
        private void Warn(string code, string message, Guid? track = null, Guid? plugin = null) => result.Issues.Add(new(StudioOneImportSeverity.Warning, code, message, track, plugin));
        private void Error(string code, string message, Guid? track = null) => result.Issues.Add(new(StudioOneImportSeverity.Error, code, message, track));
    }

    private static IEnumerable<XmlElement> Elements(XmlElement? element) => element?.ChildNodes.OfType<XmlElement>() ?? [];
    private static XmlElement? Child(XmlElement? element, string id) => Elements(element).FirstOrDefault(e => A(e, "x:id") == id || e.LocalName == id);
    private static IEnumerable<XmlElement> Child(XmlElement? element, string name, bool _) => Elements(element).Where(e => e.LocalName == name);
    private static IEnumerable<XmlElement> PluginSlots(XmlElement? element) => Elements(element).Where(e => Child(e, "deviceClassID") is not null || Child(e, "presetPath") is not null);
    private static string A(XmlElement? element, string attribute) => element?.GetAttribute(attribute) ?? "";
    private static bool Flag(XmlElement? element, string name) => A(element, name) is "1" or "true";
    private static double Number(XmlElement? element, string name, double fallback)
    {
        var text = A(element, name);
        if (text.Length == 0) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result)) throw new InvalidDataException("工程数值无效：" + name);
        return result;
    }
    private static string Name(XmlElement element)
    {
        var name = A(element, "label");
        if (name.Length == 0) name = A(element, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new InvalidDataException("Studio One 通道名称为空或过长。");
        return name;
    }
    private static string RequiredId(XmlElement element)
    {
        var id = CanonicalId(A(Child(element, "uniqueID"), "uid"));
        if (id.Length == 0) throw new InvalidDataException("Studio One 通道缺少有效标识。");
        return id;
    }
    private static string CanonicalId(string text) => Guid.TryParse(text, out var id) && id != Guid.Empty ? id.ToString("N").ToUpperInvariant() : "";
    private static string ConnectionId(XmlElement? element) => CanonicalId(A(element, "objectID").Split('/')[0]);
    private static string Metadata(XmlDocument? document, string id) => document?.GetElementsByTagName("Attribute").OfType<XmlElement>().Where(e => A(e, "id") == id).Select(e => A(e, "value")).FirstOrDefault() ?? "";
    private static string Color(string text, string fallback) => text.Length == 8 && text.All(Uri.IsHexDigit) && !text.StartsWith("00", StringComparison.Ordinal) ? "#" + text[2..].ToUpperInvariant() : fallback;
    private static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
    private static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Any(char.IsControl)
            || path.Split('/').Any(p => p is "." or "..")) throw new InvalidDataException("Studio One 工程包含无效或外部文件路径。");
        return path;
    }
    private static string Encode(object state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > RackValidation.MaximumStateBytes) throw new InvalidDataException("插件状态超过易控支持的 16 MiB。");
        return Convert.ToBase64String(bytes);
    }

    private sealed record PresetState(string ClassId, string State, bool PartialBank = false);

    private static PresetState ReadVst3(byte[] data)
    {
        if (data.Length < 48 || Text(data, 0, 4) != "VST3" || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) != 1) throw new InvalidDataException("不是支持的 VST3 预设。");
        var id = Text(data, 8, 32);
        if (!id.All(Uri.IsHexDigit)) throw new InvalidDataException("VST3 类别标识无效。");
        var listOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(40));
        if (listOffset < 48 || listOffset > data.Length - 8 || Text(data, (int)listOffset, 4) != "List") throw new InvalidDataException("VST3 状态目录无效。");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)listOffset + 4));
        if (count < 0 || count > 256 || listOffset + 8L + count * 20L > data.Length) throw new InvalidDataException("VST3 状态目录越界。");
        var state = new Dictionary<string, string>();
        for (var i = 0; i < count; i++)
        {
            var pos = (int)listOffset + 8 + i * 20;
            var type = Text(data, pos, 4);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 4));
            var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 12));
            if (offset < 48 || size < 0 || offset > listOffset || size > listOffset - offset) throw new InvalidDataException("VST3 状态块越界。");
            var key = type switch { "Comp" => "component", "Cont" => "controller", _ => null };
            if (type == "Comp" && size == 0) throw new InvalidDataException("VST3 处理器状态为空，不能确认参数已恢复。");
            if (key is null || size == 0 && type == "Cont") continue;
            if (!state.TryAdd(key, Convert.ToBase64String(data, (int)offset, (int)size))) throw new InvalidDataException("VST3 状态块重复。");
        }
        if (!state.ContainsKey("component")) throw new InvalidDataException("VST3 预设没有处理器状态。");
        return new(id.ToUpperInvariant(), Encode(state));
    }

    private static PresetState ReadVst2(byte[] data)
    {
        if (data.Length < 28 || Text(data, 0, 4) != "CcnK") throw new InvalidDataException("不是支持的 VST2 预设。");
        var declaredSize = BigInt(data, 4);
        if (declaredSize < 20 || declaredSize > data.Length - 8) throw new InvalidDataException("VST2 预设长度无效。");
        var id = BigInt(data, 16).ToString(CultureInfo.InvariantCulture);
        var kind = Text(data, 8, 4);
        if (kind == "FBCh")
        {
            if (data.Length < 160) throw new InvalidDataException("VST2 bank 头部不完整。");
            var size = BigInt(data, 156);
            if (size <= 0 || size > data.Length - 160 || size > declaredSize - 152) throw new InvalidDataException("VST2 bank 状态块越界。");
            return new(id, Encode(new { chunk = Convert.ToBase64String(data, 160, size) }));
        }
        if (kind == "FxCk") return new(id, Encode(new { program = 0, parameters = ReadVst2Parameters(data, 0, id) }));
        if (kind == "FPCh") throw new InvalidDataException("此 VST2 使用单程序 chunk，当前宿主只支持完整 bank chunk。");
        if (kind == "FxBk")
        {
            var bankEnd = declaredSize + 8;
            if (bankEnd < 156) throw new InvalidDataException("VST2 参数库头部不完整。");
            var programs = BigInt(data, 24);
            var current = BigInt(data, 12) >= 1 ? BigInt(data, 28) : 0;
            if (programs is < 1 or > 4096 || current < 0 || current >= programs) throw new InvalidDataException("VST2 参数库当前程序无效。");
            var pos = 156;
            for (var i = 0; i < programs; i++)
            {
                if (pos > bankEnd - 28) throw new InvalidDataException("VST2 参数库程序越界。");
                var size = BigInt(data, pos + 4);
                if (size < 48 || size > bankEnd - pos - 8) throw new InvalidDataException("VST2 参数库程序长度无效。");
                if (i == current) return new(id, Encode(new { program = current, parameters = ReadVst2Parameters(data, pos, id) }), true);
                pos += size + 8;
            }
        }
        throw new InvalidDataException("尚未支持的 VST2 预设种类：" + kind);
    }
    private static float[] ReadVst2Parameters(byte[] data, int offset, string id)
    {
        if (data.Length - offset < 56 || Text(data, offset, 4) != "CcnK" || Text(data, offset + 8, 4) != "FxCk"
            || BigInt(data, offset + 16).ToString(CultureInfo.InvariantCulture) != id) throw new InvalidDataException("VST2 参数程序标识无效。");
        var count = BigInt(data, offset + 24);
        if (count is < 0 or > 65536 || count * 4L > data.Length - offset - 56 || count * 4L > BigInt(data, offset + 4) - 48) throw new InvalidDataException("VST2 参数数组越界。");
        var parameters = new float[count];
        for (var i = 0; i < count; i++)
        {
            var value = BitConverter.Int32BitsToSingle(BigInt(data, offset + 56 + i * 4));
            if (!float.IsFinite(value) || value is < 0 or > 1) throw new InvalidDataException("VST2 参数值不在 0–1 范围内。");
            parameters[i] = value;
        }
        return parameters;
    }
    private static int BigInt(byte[] data, int offset) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
    private static string Text(byte[] data, int offset, int count) => Encoding.ASCII.GetString(data, offset, count);
}
