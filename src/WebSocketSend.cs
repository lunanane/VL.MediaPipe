using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;

namespace MediaPipe;

/// <summary>
/// Sends a binary message through VL.IO.WebSocket's WebSocketServer from C#: its operation SendAsync (Advanced), found by
/// reflection because the class is patched in VL (the type is emitted by the VL compiler: a precompiled package on vvvv
/// 8, compiled at load on 7.x). The same-frame readback sends from the draw with it, where no patch operation can run.
/// </summary>
/// <remarks>The operation takes (client, data, message type, cancellation token) and gives the send's Task&lt;bool&gt;,
/// as an out parameter or the return value; parameters are matched by type, so their order does not matter. SendData,
/// which the patch uses, calls the same operation.</remarks>
internal static class WebSocketSend
{
    private sealed record Binding(MethodInfo Method, int Client, int Data, int MessageType, int Token, int TaskOut);

    private static readonly ConcurrentDictionary<Type, Binding?> s_bindings = new();

    /// <summary>Why the last lookup failed, or empty.</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>Starts sending data to client; null when the server type has no usable SendAsync or the call threw.</summary>
    public static Task<bool>? Send(object server, string client, byte[] data)
    {
        var binding = s_bindings.GetOrAdd(server.GetType(), Bind);
        if (binding is null)
            return null;
        var args = new object?[binding.Method.GetParameters().Length];
        args[binding.Client] = client;
        args[binding.Data] = data;
        args[binding.MessageType] = WebSocketMessageType.Binary;
        if (binding.Token >= 0)
            args[binding.Token] = CancellationToken.None;
        try
        {
            var result = binding.Method.Invoke(server, args);
            return (binding.TaskOut >= 0 ? args[binding.TaskOut] : result) as Task<bool>;
        }
        catch (TargetInvocationException e)
        {
            Problem = $"SendAsync threw {e.InnerException?.GetType().Name}: {e.InnerException?.Message}";
            return null;
        }
    }

    private static Binding? Bind(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != "SendAsync")
                continue;
            int client = -1, data = -1, messageType = -1, token = -1, taskOut = -1;
            bool unknown = false;
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                if (t == typeof(string)) client = i;
                else if (t == typeof(IReadOnlyList<byte>)) data = i;
                else if (t == typeof(WebSocketMessageType)) messageType = i;
                else if (t == typeof(CancellationToken)) token = i;
                else if (t.IsByRef && t.GetElementType() == typeof(Task<bool>)) taskOut = i;
                else unknown = true;
            }
            if (unknown || client < 0 || data < 0 || messageType < 0)
                continue;
            if (taskOut < 0 && method.ReturnType != typeof(Task<bool>))
                continue;
            Problem = "";
            return new Binding(method, client, data, messageType, token, taskOut);
        }
        Problem = $"{type.FullName} has no SendAsync(string, IReadOnlyList<byte>, WebSocketMessageType, CancellationToken) giving a Task<bool>";
        return null;
    }
}
