namespace ion.runtime.network;

using System.Runtime.CompilerServices;

/// <summary>
/// Every live stream connection on this server — the Ion counterpart of SignalR's
/// <c>IHubContext</c>: reach any connection from anywhere, push to it, group it, close it.
/// </summary>
/// <remarks>
/// Registered as a singleton by <c>AddIonProtocol</c>. It knows the connections of this process
/// only; there is no backplane.
/// </remarks>
public interface IIonStreamConnections
{
    /// <summary>How many connections are registered right now (connecting ones included).</summary>
    int Count { get; }

    /// <summary>The connection with this id, or null.</summary>
    IIonStreamContext? Find(string connectionId);

    /// <summary>Every connection.</summary>
    IIonStreamTarget All { get; }

    /// <summary>One connection by id. Targets nothing when there is no such connection.</summary>
    IIonStreamTarget Client(string connectionId);

    /// <summary>Several connections by id.</summary>
    IIonStreamTarget Clients(IEnumerable<string> connectionIds);

    /// <summary>Every member of a group.</summary>
    IIonStreamTarget Group(string group);

    /// <summary>Every connection whose <see cref="IIonStreamContext.UserIdentifier"/> is <paramref name="userIdentifier"/> — all of a user's devices.</summary>
    IIonStreamTarget User(string userIdentifier);

    /// <summary>Every connection of one client session (one tab, one app instance).</summary>
    IIonStreamTarget Session(string sessionId);

    /// <summary>Every connection the predicate accepts.</summary>
    IIonStreamTarget Where(Func<IIonStreamContext, bool> predicate);

    /// <summary>Adds a connection to a group. A no-op for an unknown or closing connection.</summary>
    Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default);

    /// <summary>Removes a connection from a group.</summary>
    Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default);
}

/// <summary>A set of connections, resolved at the moment an operation runs.</summary>
public interface IIonStreamTarget
{
    /// <summary>The connections this target selects right now.</summary>
    IReadOnlyList<IIonStreamContext> Connections { get; }

    /// <summary>The same target minus the given connections — SignalR's <c>OthersInGroup</c> and friends.</summary>
    IIonStreamTarget Except(params string[] connectionIds);

    /// <summary>
    /// Pushes one item to every selected connection whose stream yields
    /// <typeparamref name="T"/> (or a type <typeparamref name="T"/> is assignable to — a union case
    /// goes to a stream of the union). Connections of other streams are skipped. The item is
    /// encoded once per element type, with that element type's formatter.
    /// </summary>
    /// <returns>How many connections the item was queued to.</returns>
    ValueTask<int> SendAsync<T>(T item, CancellationToken ct = default);

    /// <summary>Closes every selected connection gracefully and waits until they are gone.</summary>
    /// <returns>How many connections were closed.</returns>
    Task<int> CloseAsync(string? reason = null, bool allowReconnect = false, CancellationToken ct = default);

    /// <summary>Aborts every selected connection. Does not wait.</summary>
    /// <returns>How many connections were aborted.</returns>
    int Abort(string? reason = null);
}

/// <summary>Helpers for writing stream methods.</summary>
public static class IonStream
{
    /// <summary>
    /// A stream that yields nothing and stays open until <paramref name="ct"/> fires, then ends
    /// without throwing. Return it from a method whose items all arrive through
    /// <see cref="IIonStreamContext.SendAsync{T}"/> or <see cref="IIonStreamConnections"/> — the
    /// SignalR-hub shape.
    /// </summary>
    public static async IAsyncEnumerable<T> PushOnly<T>([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ct.CanBeCanceled)
            throw new ArgumentException(
                "A push-only stream with a token that can never fire would outlive its connection.", nameof(ct));

        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), gone))
            await gone.Task.ConfigureAwait(false);

        yield break;
    }
}
