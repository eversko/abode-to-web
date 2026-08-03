using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AbodeToWeb.Infrastructure.Logging;

public sealed class LogStreamHub
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public IAsyncEnumerable<AbodeToWebLogEvent> SubscribeAsync(
        Func<AbodeToWebLogEvent, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AbodeToWebLogEvent>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _subscriptions[id] = new Subscription(channel, predicate ?? (_ => true));
        return ReadAndRemoveAsync(id, channel.Reader, cancellationToken);
    }

    public void Publish(AbodeToWebLogEvent logEvent)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Predicate(logEvent))
                subscription.Channel.Writer.TryWrite(logEvent);
        }
    }

    private async IAsyncEnumerable<AbodeToWebLogEvent> ReadAndRemoveAsync(
        Guid id,
        ChannelReader<AbodeToWebLogEvent> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally
        {
            if (_subscriptions.TryRemove(id, out var subscription))
                subscription.Channel.Writer.TryComplete();
        }
    }

    private sealed record Subscription(Channel<AbodeToWebLogEvent> Channel, Func<AbodeToWebLogEvent, bool> Predicate);
}
