using System.Text.Json;

namespace LightDeck.App;

public sealed partial class AudioRackService
{
    private string? _stoppedHardwareMessage;

    private void ObserveHardwareReadiness(JsonElement result)
    {
        if (!_graphPrepared || Status.IsRunning || !result.TryGetProperty("hardwareReady", out var ready) ||
            ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var message = ready.GetBoolean() ? null : "机架已停止 · " + RackDiagnosticState.Reason(
            Text(result, "hardwareIssue") == "channel-range" ? "graph-channel-range" : "graph-endpoint-unresolved");
        if (message is not null || Status.Message == _stoppedHardwareMessage)
            Status = Status with { Message = message ?? "机架已停止" };
        _stoppedHardwareMessage = message;
    }
}
