﻿// The MIT License (MIT)
//
// Copyright (c) 2015 Rasmus Mikkelsen
// https://github.com/rasmus/EventFlow
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Aggregates;
using EventFlow.Core;
using EventFlow.Logs;

namespace EventFlow.EventStores.SQLite
{
    public class SQLiteEventPersistence : IEventPersistence
    {
        public class EventDataModel : ICommittedDomainEvent
        {
            public long GlobalSequenceNumber { get; set; }
            public Guid BatchId { get; set; }
            public string AggregateId { get; set; }
            public string AggregateName { get; set; }
            public string Data { get; set; }
            public string Metadata { get; set; }
            public int AggregateSequenceNumber { get; set; }
        }

        private readonly ILog _log;
        private readonly IConnection _connection;

        public SQLiteEventPersistence(
            ILog log,
            IConnection connection)
        {
            _log = log;
            _connection = connection;
        }

        public async Task<AllCommittedEventsPage> LoadAllCommittedEvents(
            GlobalPosition globalPosition,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var startPostion = globalPosition.IsStart
                ? 0
                : long.Parse(globalPosition.Value);
            var endPosition = startPostion + pageSize;

            const string sql = @"
                SELECT
                    GlobalSequenceNumber, BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber
                FROM EventFlow
                WHERE
                    GlobalSequenceNumber >= @FromId AND GlobalSequenceNumber <= @ToId
                ORDER BY
                    GlobalSequenceNumber ASC";
            var eventDataModels = await _connection.QueryAsync<EventDataModel>(
                Label.Named("mssql-fetch-events"),
                cancellationToken,
                sql,
                new
                    {
                        FromId = startPostion,
                        ToId = endPosition,
                    })
                .ConfigureAwait(false);

            var nextPosition = eventDataModels.Any()
                ? eventDataModels.Max(e => e.GlobalSequenceNumber) + 1
                : startPostion;

            return new AllCommittedEventsPage(new GlobalPosition(nextPosition.ToString()), eventDataModels);
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync(
            IIdentity id,
            IReadOnlyCollection<SerializedEvent> serializedEvents,
            CancellationToken cancellationToken)
        {
            if (!serializedEvents.Any())
            {
                return new ICommittedDomainEvent[] { };
            }

            var eventDataModels = serializedEvents
                .Select((e, i) => new EventDataModel
                    {
                        AggregateId = id.Value,
                        AggregateName = e.Metadata[MetadataKeys.AggregateName],
                        BatchId = Guid.Parse(e.Metadata[MetadataKeys.BatchId]),
                        Data = e.SerializedData,
                        Metadata = e.SerializedMetadata,
                        AggregateSequenceNumber = e.AggregateSequenceNumber,
                    })
                .ToList();

            _log.Verbose(
                "Committing {0} events to MSSQL event store for entity with ID '{1}'",
                eventDataModels.Count,
                id);

            const string sql = @"
                INSERT INTO
                    EventFlow
                        (BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber)
                    VALUES
                        (@BatchId, @AggregateId, @AggregateName, @Data, @Metadata, @AggregateSequenceNumber);
                SELECT last_insert_rowid() FROM EventFlow";

            List<long> ids = new List<long>();

            // TODO: Handle optimistic concurrency

            foreach (var eventDataModel in eventDataModels)
            {
                var row = await _connection.QueryAsync<long>(
                    Label.Named("sqlite-insert-single-event"),
                    cancellationToken,
                    sql,
                    eventDataModel)
                    .ConfigureAwait(false);
                var globalId = (row).OrderByDescending(i => i).First(); // TODO: WHY?
                ids.Add(globalId);
            }

            eventDataModels = eventDataModels
                .Zip(
                    ids,
                    (e, i) =>
                    {
                        e.GlobalSequenceNumber = i;
                        return e;
                    })
                .ToList();

            return eventDataModels;
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(
            IIdentity id,
            CancellationToken cancellationToken)
        {
            const string sql = @"
                SELECT
                    GlobalSequenceNumber, BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber
                FROM EventFlow
                WHERE
                    AggregateId = @AggregateId
                ORDER BY
                    AggregateSequenceNumber ASC";
            var eventDataModels = await _connection.QueryAsync<EventDataModel>(
                Label.Named("sqlite-fetch-events"),
                cancellationToken,
                sql,
                new
                    {
                        AggregateId = id.Value
                    })
                .ConfigureAwait(false);
            return eventDataModels;
        }

        public async Task DeleteEventsAsync(
            IIdentity id,
            CancellationToken cancellationToken)
        {
            const string sql = @"DELETE FROM EventFlow WHERE AggregateId = @AggregateId";
            var affectedRows = await _connection.ExecuteAsync(
                Label.Named("mssql-delete-aggregate"),
                cancellationToken,
                sql,
                new { AggregateId = id.Value })
                .ConfigureAwait(false);

            _log.Verbose(
                "Deleted entity with ID '{0}' by deleting all of its {1} events",
                id,
                affectedRows);
        }
    }
}