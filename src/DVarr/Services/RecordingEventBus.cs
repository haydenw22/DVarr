using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DVarr.Services;

/// <summary>
/// In-process fan-out for live recording status (docs/05 §5.3). The supervisor publishes
/// JSON deltas; each SSE client subscribes to its own bounded channel. No backplane/Redis —
/// single process, server→client only.
/// </summary>
public sealed class RecordingEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();

    public (Guid id, ChannelReader<string> reader) Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subs[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subs.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public void Publish(string json)
    {
        foreach (var ch in _subs.Values)
            ch.Writer.TryWrite(json);
    }

    public int SubscriberCount => _subs.Count;
}
