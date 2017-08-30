﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Threading;
using Rebus.Time;
using Rebus.Transport;
#pragma warning disable 1998

namespace Rebus.AzureServiceBus
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses Azure Service Bus queues to send/receive messages.
    /// </summary>
    public class BasicAzureServiceBusTransport : ITransport, IInitializable, IDisposable
    {
        const string OutgoingMessagesKey = "azure-service-bus-transport";

        static readonly TimeSpan[] RetryWaitTimes =
        {
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.1),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.2),
            TimeSpan.FromSeconds(0.5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };

        readonly ConcurrentDictionary<string, QueueClient> _queueClients = new ConcurrentDictionary<string, QueueClient>(StringComparer.InvariantCultureIgnoreCase);
        readonly NamespaceManager _namespaceManager;
        readonly string _connectionString;
        readonly IAsyncTaskFactory _asyncTaskFactory;
        readonly string _inputQueueAddress;

        readonly TimeSpan _peekLockDuration = TimeSpan.FromMinutes(5);
        readonly AsyncBottleneck _bottleneck = new AsyncBottleneck(10);
        readonly Ignorant _ignorant = new Ignorant();
        readonly ILog _log;

        readonly ConcurrentQueue<BrokeredMessage> _prefetchQueue = new ConcurrentQueue<BrokeredMessage>();
        readonly TimeSpan? _receiveTimeout;

        bool _prefetchingEnabled;
        int _numberOfMessagesToPrefetch;

        /// <summary>
        /// Constructs the transport, connecting to the service bus pointed to by the connection string.
        /// </summary>
        public BasicAzureServiceBusTransport(string connectionString, string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory, IAsyncTaskFactory asyncTaskFactory)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            if (asyncTaskFactory == null) throw new ArgumentNullException(nameof(asyncTaskFactory));

            _log = rebusLoggerFactory.GetLogger<BasicAzureServiceBusTransport>();

            _namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            _connectionString = connectionString;
            _asyncTaskFactory = asyncTaskFactory;

            if (inputQueueAddress != null)
            {
                _inputQueueAddress = inputQueueAddress.ToLowerInvariant();
            }

            // if a timeout has been specified, we respect that - otherwise, we pick a sensible default:
            _receiveTimeout = _connectionString.Contains("OperationTimeout")
                ? default(TimeSpan?)
                : TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Initializes the transport by ensuring that the input queue has been created
        /// </summary>
        public void Initialize()
        {
            _log.Info("Initializing Azure Service Bus transport with queue '{0}'", _inputQueueAddress);

            if (_inputQueueAddress != null)
            {
                CreateQueue(_inputQueueAddress);
            }
        }

        /// <summary>
        /// Purges the input queue by deleting it and creating it again
        /// </summary>
        public void PurgeInputQueue()
        {
            if (_inputQueueAddress == null) return;

            _log.Info("Purging queue '{0}'", _inputQueueAddress);
            _namespaceManager.DeleteQueue(_inputQueueAddress);

            CreateQueue(_inputQueueAddress);
        }

        /// <summary>
        /// Configures the transport to prefetch the specified number of messages into an in-mem queue for processing, disabling automatic peek lock renewal
        /// </summary>
        public void PrefetchMessages(int numberOfMessagesToPrefetch)
        {
            _prefetchingEnabled = true;
            _numberOfMessagesToPrefetch = numberOfMessagesToPrefetch;
        }

        /// <summary>
        /// Enables automatic peek lock renewal - only recommended if you truly need to handle messages for a very long time
        /// </summary>
        public bool AutomaticallyRenewPeekLock { get; set; }

        /// <summary>
        /// Creates a queue with the given address
        /// </summary>
        public void CreateQueue(string address)
        {
            if (DoNotCreateQueuesEnabled)
            {
                _log.Info("Transport configured to not create queue - skipping existencecheck and potential creation");
                return;
            }
            
            if (_namespaceManager.QueueExists(address)) return;

            var queueDescription = new QueueDescription(address)
            {
                MaxSizeInMegabytes = 1024,
                MaxDeliveryCount = 100,
                LockDuration = _peekLockDuration,
                EnablePartitioning = PartitioningEnabled
            };

            try
            {
                _log.Info("Input queue '{0}' does not exist - will create it now", _inputQueueAddress);
                _namespaceManager.CreateQueue(queueDescription);
                _log.Info("Created!");
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                // fair enough...
                _log.Info("MessagingEntityAlreadyExistsException - carrying on");
            }
        }

        /// <summary>
        /// Sends the given message to the queue with the given <paramref name="destinationAddress"/>
        /// </summary>
        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            GetOutgoingMessages(context)
                .GetOrAdd(destinationAddress, _ => new ConcurrentQueue<TransportMessage>())
                .Enqueue(message);
        }

        /// <summary>
        /// Should return a new <see cref="Retrier"/>, fully configured to correctly "accept" the right exceptions
        /// </summary>
        static Retrier GetRetrier()
        {
            return new Retrier(RetryWaitTimes)
                .On<MessagingException>(e => e.IsTransient)
                .On<MessagingCommunicationException>(e => e.IsTransient)
                .On<ServerBusyException>(e => e.IsTransient);
        }

        /// <summary>
        /// Receives the next message from the input queue. Returns null if no message was available
        /// </summary>
        public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
        {
            if (_inputQueueAddress == null)
            {
                throw new InvalidOperationException("This Azure Service Bus transport does not have an input queue, hence it is not possible to reveive anything");
            }

            using (await _bottleneck.Enter(cancellationToken))
            {
                var brokeredMessage = await ReceiveBrokeredMessage();

                if (brokeredMessage == null) return null;

                var headers = brokeredMessage.Properties
                    .Where(kvp => kvp.Value is string)
                    .ToDictionary(kvp => kvp.Key, kvp => (string)kvp.Value);

                var messageId = headers.GetValueOrNull(Headers.MessageId);
                var leaseDuration = (brokeredMessage.LockedUntilUtc - DateTime.UtcNow);
                var lockRenewalInterval = TimeSpan.FromMinutes(0.5 * leaseDuration.TotalMinutes);

                var renewalTask = GetRenewalTaskOrFakeDisposable(messageId, brokeredMessage, lockRenewalInterval);

                context.OnAborted(() =>
                {
                    renewalTask.Dispose();

                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        // if it fails, it'll be back on the queue anyway....
                        _log.Warn("Could not abandon message: {0}", exception);
                    }
                });

                context.OnCommitted(async () => renewalTask.Dispose());

                context.OnCompleted(async () =>
                {
                    await brokeredMessage.CompleteAsync();
                });

                context.OnDisposed(() =>
                {
                    renewalTask.Dispose();
                    brokeredMessage.Dispose();
                });

                using (var memoryStream = new MemoryStream())
                {
                    await brokeredMessage.GetBody<Stream>().CopyToAsync(memoryStream);
                    return new TransportMessage(headers, memoryStream.ToArray());
                }
            }
        }

        ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>> GetOutgoingMessages(ITransactionContext context)
        {
            return context.GetOrAdd(OutgoingMessagesKey, () =>
            {
                var destinations = new ConcurrentDictionary<string, ConcurrentQueue<TransportMessage>>();

                context.OnCommitted(async () =>
                {
                    // send outgoing messages
                    foreach (var destinationAndMessages in destinations)
                    {
                        var destinationAddress = destinationAndMessages.Key;
                        var messages = destinationAndMessages.Value;

                        var sendTasks = messages
                            .Select(async message =>
                            {
                                await GetRetrier().Execute(async () =>
                                {
                                    using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                                    using (var brokeredMessageToSend = MsgHelpers.CreateBrokeredMessage(message))
                                    {
                                        try
                                        {
                                            await GetQueueClient(destinationAddress).SendAsync(brokeredMessageToSend);
                                        }
                                        catch (MessagingEntityNotFoundException exception)
                                        {
                                            // do NOT rethrow as MessagingEntityNotFoundException because it has its own ToString that swallows most of the info!!
                                            throw new MessagingException($"Could not send to '{destinationAddress}'!", false, exception);
                                        }
                                    }
                                });
                            })
                            .ToArray();

                        await Task.WhenAll(sendTasks);
                    }
                });

                return destinations;
            });
        }

        IDisposable GetRenewalTaskOrFakeDisposable(string messageId, BrokeredMessage brokeredMessage, TimeSpan lockRenewalInterval)
        {
            if (AutomaticallyRenewPeekLock)
            {
                var renewalTask = _asyncTaskFactory.Create($"RenewPeekLock-{messageId}",
                    async () =>
                    {
                        var nowUtc = RebusTime.Now.UtcDateTime;
                        var lockedUntilUtc = brokeredMessage.LockedUntilUtc;

                        if (lockedUntilUtc - nowUtc > TimeSpan.FromMinutes(1)) return;

                        _log.Info("Renewing peek lock for message with ID {0} (time is now {1}, the message is locked until {2})", messageId, nowUtc, lockedUntilUtc);

                        await brokeredMessage.RenewLockAsync();

                        _log.Info("Peek look renewed - message is now locked until {0}", brokeredMessage.LockedUntilUtc);
                    },
                    intervalSeconds: (int) 30,
                    prettyInsignificant: true);

                renewalTask.Start();

                return renewalTask;
            }

            return new FakeDisposable();
        }

        class FakeDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        async Task<BrokeredMessage> ReceiveBrokeredMessage()
        {
            var queueAddress = _inputQueueAddress;

            if (_prefetchingEnabled)
            {
                BrokeredMessage nextMessage;

                if (_prefetchQueue.TryDequeue(out nextMessage))
                {
                    return nextMessage;
                }

                var client = GetQueueClient(queueAddress);

                // Timeout can be specified in ASB ConnectionString Endpoint=sb:://...;OperationTimeout=00:00:10
                var brokeredMessages = _receiveTimeout.HasValue
                    ? (await client.ReceiveBatchAsync(_numberOfMessagesToPrefetch, _receiveTimeout.Value)).ToList()
                    : (await client.ReceiveBatchAsync(_numberOfMessagesToPrefetch)).ToList();

                _ignorant.Reset();

                if (!brokeredMessages.Any()) return null; 

                foreach (var receivedMessage in brokeredMessages)
                {
                    _prefetchQueue.Enqueue(receivedMessage);
                }

                _prefetchQueue.TryDequeue(out nextMessage);

                return nextMessage; //< just accept null at this point if there was nothing
            }

            try
            {
                // Timeout can be specified in ASB ConnectionString Endpoint=sb:://...;OperationTimeout=00:00:10
                var brokeredMessage = _receiveTimeout.HasValue
                    ? await GetQueueClient(queueAddress).ReceiveAsync(_receiveTimeout.Value)
                    : await GetQueueClient(queueAddress).ReceiveAsync();

                _ignorant.Reset();

                return brokeredMessage;
            }
            catch (Exception exception)
            {
                if (_ignorant.IsToBeIgnored(exception)) return null;

                QueueClient possiblyFaultyQueueClient;

                if (_queueClients.TryRemove(queueAddress, out possiblyFaultyQueueClient))
                {
                    CloseQueueClient(possiblyFaultyQueueClient);
                }

                throw;
            }
        }

        static void CloseQueueClient(QueueClient queueClientToClose)
        {
            try
            {
                queueClientToClose.Close();
            }
            catch (Exception)
            {
                // ignored because we don't care!
            }
        }

        QueueClient GetQueueClient(string queueAddress)
        {
            var queueClient = _queueClients.GetOrAdd(queueAddress, address =>
            {
                _log.Debug("Initializing new queue client for {0}", address);

                var newQueueClient = QueueClient.CreateFromConnectionString(_connectionString, address, ReceiveMode.PeekLock);

                return newQueueClient;
            });

            return queueClient;
        }

        /// <summary>
        /// Gets the address of the input queue for the transport
        /// </summary>
        public string Address => _inputQueueAddress;

        /// <summary>
        /// Gets/sets whether partitioning should be enabled on new queues. Only takes effect for queues created
        /// after the property has been enabled
        /// </summary>
        public bool PartitioningEnabled { get; set; }

        /// <summary>
        /// Gets/sets whether to skip creating queues
        /// </summary>
        public bool DoNotCreateQueuesEnabled { get; set; }

        /// <summary>
        /// Releases prefetched messages and cached queue clients
        /// </summary>
        public void Dispose()
        {
            DisposePrefetchedMessages();

            _queueClients.Values.ForEach(CloseQueueClient);
        }

        void DisposePrefetchedMessages()
        {
            BrokeredMessage brokeredMessage;
            while (_prefetchQueue.TryDequeue(out brokeredMessage))
            {
                using (brokeredMessage)
                {
                    try
                    {
                        brokeredMessage.Abandon();
                    }
                    catch (Exception exception)
                    {
                        _log.Warn("Could not abandon brokered message with ID {0}: {1}", brokeredMessage.MessageId, exception);
                    }
                }
            }
        }         
    }
}