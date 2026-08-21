using System.Text;
using Harbora.Application.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// The real half of F2's broker seam (2026-08-21 functions-and-services plan, "Queue-triggered
/// functions"): speaks AMQP to a RabbitMQ managed service via <c>RabbitMQ.Client</c>.
///
/// <para>
/// <b>Unproven on this machine.</b> There is no Docker and no live RabbitMQ here, so nothing about
/// this class's actual protocol round-trip has been exercised — only that it compiles against the
/// client library's 7.x async API. <see cref="QueueFunctionConsumerHost"/>'s own tests run against
/// a fake <see cref="IQueueBrokerConnectionFactory"/> instead, which is what proves the consumer's
/// ack/nack/dead-letter/reconnect behaviour.
/// </para>
/// </summary>
public sealed class RabbitMqBrokerConnectionFactory : IQueueBrokerConnectionFactory
{
    /// <summary>
    /// Short on purpose: a broker that will not answer must be discovered quickly by the consumer's
    /// own retry loop, not left to hang the one reconciliation tick that owns every other queue
    /// function too.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public async Task<IQueueBrokerConnection> ConnectAsync(QueueBrokerAddress address, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = address.Host,
            Port = address.Port,
            UserName = address.User,
            Password = address.Password,
            RequestedConnectionTimeout = ConnectTimeout
        };

        var connection = await factory.CreateConnectionAsync(ct);
        try
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: ct);
            // This bridge is one panel-side consumer, not a scaled pool — said plainly in the editor
            // too. Prefetch 1 keeps that honest at the protocol level: never more than one message
            // outstanding at a time.
            await channel.BasicQosAsync(0, prefetchCount: 1, global: false, ct);
            return new RabbitMqBrokerConnection(connection, channel);
        }
        catch
        {
            await connection.CloseAsync();
            connection.Dispose();
            throw;
        }
    }
}

internal sealed class RabbitMqBrokerConnection(IConnection connection, IChannel channel) : IQueueBrokerConnection
{
    public async Task ConsumeAsync(
        string queueName, Func<QueueDelivery, CancellationToken, Task<QueueAckOutcome>> handle, CancellationToken ct)
    {
        // Durable (survives a broker restart), not exclusive, not auto-delete: a publisher — or
        // another consumer that reaches this queue before or after this one does — must still find
        // it. Idempotent: declaring a queue that already exists with the same properties is a no-op.
        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);

        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, args) =>
        {
            faulted.TrySetException(new InvalidOperationException(
                $"The channel closed: {args.ReplyText} ({args.ReplyCode})."));
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var body = Encoding.UTF8.GetString(delivery.Body.ToArray());
            QueueAckOutcome outcome;
            try
            {
                outcome = await handle(new QueueDelivery(body, delivery.Redelivered), ct);
            }
            catch
            {
                // The handler's own contract is to return a verdict, never throw — but a broker
                // message must not be left neither acked nor nacked over a bug in the caller, or it
                // sits unacked until the connection eventually drops and the broker redelivers it
                // anyway, just later and less predictably. Treat an unexpected throw as the same
                // "first failure" a returned NackRequeue would have been.
                outcome = delivery.Redelivered ? QueueAckOutcome.NackDrop : QueueAckOutcome.NackRequeue;
            }

            switch (outcome)
            {
                case QueueAckOutcome.Ack:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
                    break;
                case QueueAckOutcome.NackRequeue:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
                    break;
                default:
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
                    break;
            }
        };

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, ct);

        // Held open until the caller cancels or the channel reports it is gone — either way this
        // returns (or throws), so the caller's own reconnect loop notices rather than the worker
        // going quiet with nobody told.
        using var registration = ct.Register(() => faulted.TrySetCanceled(ct));
        await faulted.Task;
    }

    public async ValueTask DisposeAsync()
    {
        try { await channel.CloseAsync(); } catch { /* best effort */ }
        try { await connection.CloseAsync(); } catch { /* best effort */ }
        channel.Dispose();
        connection.Dispose();
    }
}
