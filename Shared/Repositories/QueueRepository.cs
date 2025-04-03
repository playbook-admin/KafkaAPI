﻿using System;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Shared.Models;

namespace Shared.Repositories;
public class QueueRepository : IQueueRepository
{
    private readonly string _bootstrapServers = "host.docker.internal:9092";
    private readonly string _clientQueueTopic = "client-queue";
    private readonly string _serverQueueTopic = "server-queue";

    public async Task<int> AddClientQueueItemAsync(QueueEntity entity)
    {
        return await ProduceMessageAsync(_clientQueueTopic, entity);
    }

    public async Task<int> AddServerQueueItemAsync(QueueEntity entity)
    {
        return await ProduceMessageAsync(_serverQueueTopic, entity);
    }

    public async Task<QueueEntity?> GetMessageFromClientQueueAsync()
    {
        return await ConsumeMessageAsync(_clientQueueTopic);
    }

    public async Task<QueueEntity?> GetMessageFromServerByCorrelationIdAsync(Guid correlationId)
    {
        return await ConsumeMessageAsync(_serverQueueTopic, correlationId);
    }

    private async Task<int> ProduceMessageAsync(string topic, QueueEntity entity)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            Acks = Acks.Leader,  // Snabbare leverans
            LingerMs = 5,        // Minska fördröjning innan meddelandet skickas
            BatchNumMessages = 10 // Skickar i batchar
        };

        using var producer = new ProducerBuilder<Null, string>(config).Build();
        var jsonMessage = JsonSerializer.Serialize(entity);

        try
        {
            var result = await producer.ProduceAsync(topic, new Message<Null, string> { Value = jsonMessage });
            return result.Status == PersistenceStatus.Persisted ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Producing message failed: {ex.Message}");
            return 0;
        }
    }

    private async Task<QueueEntity?> ConsumeMessageAsync(string topic, Guid? correlationId = null)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = "queue-group",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            GroupInstanceId = (topic == _clientQueueTopic) ? _clientQueueTopic : _serverQueueTopic,
            EnableAutoCommit = false, // Viktigt för att hantera egna commits
            AllowAutoCreateTopics = false, // Förhindra oväntad topic-skapande
            MaxPollIntervalMs = 300000, // 5 minuter för att hantera långsam bearbetning
            SessionTimeoutMs = 45000, // Standard timeout
            EnablePartitionEof = true // Möjliggör EOF-hantering
        };


        using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
        consumer.Subscribe(topic);

        try
        {
            while (true)
            {
                var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1)); // Vänta 1 sekund

                if (consumeResult?.Message==null)
                {
                    await Task.Delay(100);  // Vänta 100ms för att minska CPU-belastning
                    continue;
                }

                var entity = JsonSerializer.Deserialize<QueueEntity>(consumeResult.Message.Value);

                if (correlationId == null || entity?.CorrelationId == correlationId)
                {
                    consumer.Commit(consumeResult); // Manuell commit av offset
                    return entity;
                }
            }
        }
        catch (KafkaException kafkaEx) when (kafkaEx.Error.IsFatal)
        {
            // If topic does not exist or another fatal error occurs, return null
            Console.WriteLine($"Topic not found or fatal error: {kafkaEx.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Consuming message failed: {ex.Message}");
            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

}
