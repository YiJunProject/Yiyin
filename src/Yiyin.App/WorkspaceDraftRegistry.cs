using System.Windows;

namespace LightDeck.App;

internal sealed record WorkspaceDraft(Window Owner, string Label, Func<bool> HasPending, Func<Task<bool>> Apply, Action Discard);
internal static class WorkspaceDraftRegistry
{
    private static readonly List<WorkspaceDraft> Entries = [];
    public static IDisposable Register(Window owner, string label, Func<bool> hasPending, Func<Task<bool>> apply, Action discard)
    {
        var entry = new WorkspaceDraft(owner, label, hasPending, apply, discard); Entries.Add(entry);
        var registration = new Registration(entry); owner.Closed += (_, _) => registration.Dispose(); return registration;
    }
    public static WorkspaceDraft[] Pending => Entries.Where(e => e.HasPending()).ToArray();
    private sealed class Registration(WorkspaceDraft entry) : IDisposable { public void Dispose() => Entries.Remove(entry); }
}
