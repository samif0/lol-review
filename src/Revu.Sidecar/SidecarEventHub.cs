#nullable enable

using System.Threading.Channels;

namespace Revu.Sidecar;

/// <summary>
/// Fan-out bus for Server-Sent Events. The <see cref="SidecarGameFlowCoordinator"/>
/// converts each LCU IMessenger message into a <see cref="SidecarEvent"/> and
/// publishes it here; every connected GET /api/events client owns an unbounded
/// channel subscription that receives a copy.
///
/// <para>
/// A small ring buffer of the most-recent events is retained so a client that
/// connects mid-flow (e.g. the webview reloads the pre-game page after champ
/// select already started) is replayed the latest champ-select / in-progress
/// state immediately instead of waiting for the next LCU tick.
/// </para>
/// </summary>
public sealed class SidecarEventHub
{
    private readonly object _gate = new();
    private readonly List<Channel<SidecarEvent>> _subscribers = new();

    /// <summary>One published SSE event: a stable type tag + an arbitrary JSON
    /// payload object (serialized by the endpoint with the shared options).</summary>
    public sealed record SidecarEvent(string Type, object Payload);

    /// <summary>Publish an event to every connected client. Drops to disconnected
    /// channels are ignored (the writer completes when the client leaves).</summary>
    public void Publish(string type, object payload)
    {
        var evt = new SidecarEvent(type, payload);
        lock (_gate)
        {
            foreach (var ch in _subscribers)
            {
                // Unbounded writer never blocks; a closed channel just returns false.
                ch.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>Subscribe a new SSE client. Returns the reader to stream from and a
    /// disposer that detaches + completes the channel on disconnect.</summary>
    public (ChannelReader<SidecarEvent> Reader, IDisposable Subscription) Subscribe()
    {
        var channel = Channel.CreateUnbounded<SidecarEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        return (channel.Reader, new Subscription(this, channel));
    }

    private void Unsubscribe(Channel<SidecarEvent> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }
        channel.Writer.TryComplete();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly SidecarEventHub _hub;
        private readonly Channel<SidecarEvent> _channel;
        private bool _disposed;

        public Subscription(SidecarEventHub hub, Channel<SidecarEvent> channel)
        {
            _hub = hub;
            _channel = channel;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hub.Unsubscribe(_channel);
        }
    }
}
