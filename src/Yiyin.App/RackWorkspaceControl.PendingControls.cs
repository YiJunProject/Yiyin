using LightDeck.Core;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    // One latest absolute assignment per exact control; dragging while a native
    // flush is pending cannot create an ever-growing list or replay toggle actions.
    private sealed class PendingControlEdits
    {
        private readonly Dictionary<string, Action<RackTrack>> _fields = [];
        public void Capture(RackTrack before, RackTrack after)
        {
            void Field<T>(string key, T previous, T value, Action<RackTrack, T> assign)
            { if (!EqualityComparer<T>.Default.Equals(previous, value)) _fields[key] = track => assign(track, value); }
            Field("gain", before.GainDb, after.GainDb, (t, v) => t.GainDb = v);
            Field("solo", before.Solo, after.Solo, (t, v) => t.Solo = v);
            Field("soloSafe", before.SoloSafe, after.SoloSafe, (t, v) => t.SoloSafe = v);
            Field("pan", before.Pan, after.Pan, (t, v) => t.Pan = v);
            if (after.Kind is RackTrackKind.Mono or RackTrackKind.Stereo && (before.Mute != after.Mute || before.Monitor != after.Monitor))
            {
                // Input power is a single intent with two fields. Preserve the
                // pair even when only one differs from the optimistic view.
                var mute = after.Mute; var monitor = after.Monitor;
                _fields["inputPower"] = track => { track.Mute = mute; track.Monitor = monitor; };
            }
            else
            {
                Field("mute", before.Mute, after.Mute, (t, v) => t.Mute = v);
                Field("monitor", before.Monitor, after.Monitor, (t, v) => t.Monitor = v);
            }
            Field("mainEnabled", before.MainOutputEnabled, after.MainOutputEnabled, (t, v) => t.MainOutputEnabled = v);
            Field("mainGain", before.MainOutputGainDb, after.MainOutputGainDb, (t, v) => t.MainOutputGainDb = v);
            foreach (var send in after.Sends)
            {
                var old = before.Sends.FirstOrDefault(s => s.Id == send.Id); if (old is null) continue;
                var id = send.Id;
                Field("sendGain/" + id, old.GainDb, send.GainDb, (t, v) => { if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.GainDb = v; });
                Field("sendEnabled/" + id, old.Enabled, send.Enabled, (t, v) => { if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.Enabled = v; });
                Field("sendPre/" + id, old.PreFader, send.PreFader, (t, v) => { if (t.Sends.FirstOrDefault(s => s.Id == id) is { } current) current.PreFader = v; });
            }
            foreach (var plugin in after.Inserts)
            {
                var old = before.Inserts.FirstOrDefault(p => p.Id == plugin.Id && p.Identity.Key == plugin.Identity.Key); if (old is null) continue;
                var id = plugin.Id; var identity = plugin.Identity.Key;
                Field("wet/" + id, old.Wet, plugin.Wet, (t, v) => { if (t.Inserts.FirstOrDefault(p => p.Id == id && p.Identity.Key == identity) is { } current) current.Wet = v; });
                Field("bypass/" + id, old.Bypass, plugin.Bypass, (t, v) => { if (t.Inserts.FirstOrDefault(p => p.Id == id && p.Identity.Key == identity) is { } current) current.Bypass = v; });
            }
        }
        public void Apply(RackTrack track) { foreach (var field in _fields.Values) field(track); }
    }
}
