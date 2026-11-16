using System.Net.Sockets;

namespace LightDeck.Core;

public enum DiagnosticLevel { Info, Warning, Error }

/// <summary>Small non-throwing producer API. Callers supply fixed explanations and allowlisted facts,
/// never raw protocol frames, credentials, plugin state, or exception messages.</summary>
public static class DiagnosticLog
{
    private static DiagnosticLogStore? _store;
    public static DiagnosticLogStore? Current => Volatile.Read(ref _store);
    public static void Use(DiagnosticLogStore? store) => Volatile.Write(ref _store, store);

    public static void Write(string category, string code, DiagnosticLevel level, string summary, string reason,
        params (string Key, object? Value)[] details)
    {
        try { Current?.Write(category, code, level, summary, reason, details); }
        catch { /* Diagnostics must never stop control or audio. */ }
    }

    public static (string Key, object? Value)[] ExceptionFacts(Exception exception)
    {
        var socket = exception as SocketException ?? exception.InnerException as SocketException;
        return [("exceptionType", exception.GetType().Name), ("hresult", exception.HResult),
            ("socketError", socket?.SocketErrorCode.ToString()), ("socketNativeError", socket?.NativeErrorCode)];
    }
}
