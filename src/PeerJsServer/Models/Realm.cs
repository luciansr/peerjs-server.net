﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeerJs.Helpers;

namespace PeerJs.Models
{
    public interface IRealm
    {
        IEnumerable<string> GetClientIds();

        IClient GetClient(string clientId);

        IEnumerable<string> GetClientIdsWithQueue();

        void SetClient(IClient client);

        bool RemoveClientById(string clientId);

        IMessageQueue GetMessageQueueById(string clientId);

        void AddMessageToQueue(string clientId, Message msg);

        void ClearMessageQueue(string clientId);

        string GenerateClientId();

        Task HandleMessageAsync(IClient client, Message message, CancellationToken cancellationToken = default);
    }

    public class Realm : IRealm
    {
        private readonly ConcurrentDictionary<string, IClient> _clients;
        private readonly ConcurrentDictionary<string, IMessageQueue> _messageQueues;

        public Realm()
        {
            _clients = new ConcurrentDictionary<string, IClient>();
            _messageQueues = new ConcurrentDictionary<string, IMessageQueue>();
        }

        public void AddMessageToQueue(string clientId, Message msg)
        {
            if (_messageQueues.TryGetValue(clientId, out var messageQueue))
            {
                messageQueue.Enqueue(msg);
            }
            else
            {
                var  clientQueue = new MessageQueue();
                clientQueue.Enqueue(msg);
                _messageQueues.TryAdd(clientId, clientQueue);
            }
        }

        public void ClearMessageQueue(string clientId)
        {
            _messageQueues.TryRemove(clientId, out _);
        }

        public IMessageQueue GetMessageQueueById(string clientId)
        {
            return _messageQueues.TryGetValue(clientId, out var messageQueue) ? messageQueue : null;
        }

        public IClient GetClient(string clientId)
        {
            return _clients.TryGetValue(clientId, out var client) ? client : null;
        }

        public IEnumerable<string> GetClientIds()
        {
            return _clients.Keys;
        }

        public IEnumerable<string> GetClientIdsWithQueue()
        {
            return _messageQueues.Keys;
        }

        public void SetClient(IClient client)
        {
            _clients.TryAdd(client.GetId(), client);
        }

        public bool RemoveClientById(string clientId)
        {
            return _clients.TryRemove(clientId, out _);
        }

        public string GenerateClientId()
        {
            return Guid.NewGuid().ToString();
        }

        public Task HandleMessageAsync(IClient client, Message message, CancellationToken cancellationToken = default)
        {
            return message.Type switch
            {
                MessageType.Open => AcceptAsync(client, cancellationToken),
                MessageType.Heartbeat => HeartbeatAsync(client),
                MessageType.Offer => TransferAsync(client, message, cancellationToken),
                MessageType.Answer => TransferAsync(client, message, cancellationToken),
                MessageType.Candidate => TransferAsync(client, message, cancellationToken),
                MessageType.Expire => TransferAsync(client, message, cancellationToken),
                MessageType.Leave => LeaveAsync(client, message, cancellationToken),
                _ => throw new NotImplementedException(),
            };
        }

        private Task AcceptAsync(IClient client, CancellationToken cancellationToken = default)
        {
            return client.SendAsync(new Message
            {
                Type = MessageType.Open,
            }, cancellationToken);
        }

        private Task HeartbeatAsync(IClient client)
        {
            client.SetLastHeartbeat(DateTime.UtcNow);

            return Task.CompletedTask;
        }

        private async Task TransferAsync(IClient client, Message message, CancellationToken cancellationToken = default)
        {
            var destinationClient = GetClient(message.Destination);

            if (destinationClient != null)
            {
                try
                {
                    message.Source = client.GetId();
                    // if (message.Type == "OFFER")
                    // {
                    //     Console.WriteLine($"OFFER {message.Source} - {message.Destination}");
                    // }

                    await destinationClient.SendAsync(message, cancellationToken);
                }
                catch (Exception ex)
                {
                    var destinationSocket = destinationClient.GetSocket();

                    // This happens when a peer disconnects without closing connections and
                    // the associated WebSocket has not closed.
                    if (destinationSocket != null)
                    {
                        await destinationSocket.CloseAsync(ex.Message);
                    }
                    else
                    {
                        RemoveClientById(message.Destination);
                    }

                    // Tell the other side to stop trying.
                    await LeaveAsync(destinationClient, new Message
                    {
                        Destination = message.Source,
                        Source = message.Destination,
                        Type = MessageType.Leave,
                    }, cancellationToken);
                }
            }
            // else
            // {
            //     // if (message.ShouldEnqueue())
            //     // {
            //     //     AddMessageToQueue(message.Destination, message);
            //     // }
            // }
        }

        private async Task LeaveAsync(IClient client, Message message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message.Destination))
            {
                RemoveClientById(message.Source);

                return;
            }

            await TransferAsync(client, message, cancellationToken);
        }
    }
}