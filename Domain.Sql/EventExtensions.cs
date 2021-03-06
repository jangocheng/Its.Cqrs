// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using Microsoft.Its.Domain.Serialization;
using Newtonsoft.Json;

namespace Microsoft.Its.Domain.Sql
{
    /// <summary>
    /// Provides methods for working with events.
    /// </summary>
    public static class EventExtensions
    {
        internal static readonly Lazy<JsonSerializerSettings> serializerSettings = new Lazy<JsonSerializerSettings>(() =>
        {
            var settings = Serializer.CloneSettings();
            settings.ContractResolver = Serializer.AreDefaultSerializerSettingsConfigured ? new EventContractResolver() : settings.ContractResolver;
            return settings;
        });

        /// <summary>
        /// Creates a <see cref="StorableEvent" /> based on the specified domain event.
        /// </summary>
        /// <param name="domainEvent">The domain event.</param>
        public static StorableEvent ToStorableEvent(this IEvent domainEvent)
        {
            if (domainEvent == null)
            {
                throw new ArgumentNullException(nameof(domainEvent));
            }

            string eventStreamName = null;
            var aggregateType = domainEvent.AggregateType();
            eventStreamName = aggregateType != null
                                  ? AggregateType.EventStreamName(aggregateType)
                                  : ((dynamic) domainEvent).EventStreamName;

            return new StorableEvent
            {
                Actor = domainEvent.Actor(),
                StreamName = eventStreamName,
                SequenceNumber = domainEvent.SequenceNumber,
                AggregateId = domainEvent.AggregateId,
                Type = domainEvent.EventName(),
                Body = JsonConvert.SerializeObject(domainEvent, Formatting.None, serializerSettings.Value),
                Timestamp = domainEvent.Timestamp,
                ETag = domainEvent.ETag,
                Id = domainEvent.AbsoluteSequenceNumber()
            };
        }

        /// <summary>
        /// Creates a <see cref="StorableEvent" /> based on the specified domain event.
        /// </summary>
        /// <param name="domainEvent">The domain event.</param>
        /// <param name="serialize">Serializes the domain event.</param>
        internal static StorableEvent ToStorableEvent<TAggregate>(this IEvent<TAggregate> domainEvent, SerializeEvent serialize)
            where TAggregate : IEventSourced
        {
            return new StorableEvent
            {
                Actor = domainEvent.Actor(),
                StreamName = AggregateType<TAggregate>.EventStreamName,
                SequenceNumber = domainEvent.SequenceNumber,
                AggregateId = domainEvent.AggregateId,
                Type = domainEvent.EventName(),
                Body = serialize(domainEvent),
                Timestamp = domainEvent.Timestamp,
                ETag = domainEvent.ETag
            };
        }

        /// <summary>
        /// Creates a domain event from a <see cref="StorableEvent" />.
        /// </summary>
        /// <param name="storableEvent">The storable event.</param>
        /// <param name="deserialize">Deserializes the storable event.</param>
        /// <returns>A deserialized domain event.</returns>
        public static IEvent ToDomainEvent(this StorableEvent storableEvent, DeserializeEvent deserialize = null) =>
            Serializer.DeserializeEvent(
                storableEvent.StreamName,
                storableEvent.Type,
                storableEvent.AggregateId,
                storableEvent.SequenceNumber,
                storableEvent.Timestamp,
                storableEvent.Body,
                storableEvent.Id,
                serializerSettings.Value,
                deserialize: deserialize,
                etag: storableEvent.ETag);
    }
}