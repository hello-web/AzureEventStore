﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lokad.AzureEventStore.Projections;
using Lokad.AzureEventStore.Quarantine;
using Lokad.AzureEventStore.Streams;

namespace Lokad.AzureEventStore.Wrapper
{
    /// <summary>
    /// Single-threaded wrapper around an event stream and corresponding 
    /// projections.
    /// </summary>
    internal sealed class EventStreamWrapper<TEvent, TState> where TState : class where TEvent : class
    {        
        /// <summary> The projections that keep the state.  </summary>
        private readonly ReifiedProjectionGroup<TEvent, TState> _projection;

        /// <summary> The stream from which events are read. </summary>
        internal readonly EventStream<TEvent> Stream;

        /// <summary> Quarantined events. </summary>
        public readonly EventQuarantine<TEvent> Quarantine = new EventQuarantine<TEvent>();

        /// <summary> Logging status messages. </summary>
        private readonly ILogAdapter _log;

        public EventStreamWrapper(StorageConfiguration storage, IEnumerable<IProjection<TEvent>> projections, IProjectionCacheProvider projectionCache, ILogAdapter log = null)
        {
            _log = log;
            Stream = new EventStream<TEvent>(storage, log);
            _projection = new ReifiedProjectionGroup<TEvent, TState>(projections, projectionCache, log);
        }

        /// <summary> The current synchronization step. </summary>
        /// <remarks>
        /// Incremented each time <see cref="Current"/> catches up with the 
        /// remote stream (or when a read attempt concludes that there is
        /// nothing to catch up with).
        /// 
        /// If this property has e.g. value 14 now, and had value 13 at time T, then 
        /// all the events present on the remote stream up to time T are currently
        /// taken into account. This can be used to implement "is up-to-date" 
        /// requirements on the state.  
        /// </remarks>
        public uint SyncStep { get; private set; }

        /// <summary> The current state. </summary>
        public TState Current => _projection.Current;

        /// <summary> The current sequence number. </summary>
        // Could also be '_projection.Sequence', though this is not true in
        // the case of deserialization errors.
        public uint Sequence => Stream.Sequence;

        /// <summary>
        /// Reads up events up to the last one available. Pre-loads the projection from its cache,
        /// if available, to reduce the necessary work.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancel = default(CancellationToken))
        {
            var sw = Stopwatch.StartNew();

            try
            {
                // Load project and discard events before that.
                _log?.Info($"{sw.Elapsed:mm':'ss'.'ff} [ES init] loading projections.");
                await _projection.TryLoadAsync(cancel).ConfigureAwait(false);

                var catchUp = _projection.Sequence + 1;
                if (_onEachCommitted != null)
                    foreach (var e in _onEachCommitted)
                        if (e.Start < catchUp) catchUp = e.Start;

                _log?.Info($"{sw.Elapsed:mm':'ss'.'ff} [ES init] advancing stream to seq {catchUp}.");
                await Stream.DiscardUpTo(catchUp, cancel).ConfigureAwait(false);

                if (Stream.Sequence < catchUp)
                {
                    _log?.Warning(
                        $"{sw.Elapsed:mm':'ss'.'ff} [ES init] invalid seq {catchUp} > {Stream.Sequence}, resetting everything.");

                    // Cache is apparently beyond the available sequence. Could happen in 
                    // development environments with non-persistent events but persistent 
                    // caches. Treat cache as invalid and start from the beginning.
                    Stream.Reset();
                    _projection.Reset();
                }
            }
            catch (Exception e)
            {
                _log?.Warning(
                    $"{sw.Elapsed:mm':'ss'.'ff} [ES init] error while reading cache.", e);

                // Something went wrong when reading the cache. Stop.
                Stream.Reset();
                _projection.Reset();
            }
            
            // Start reading everything
            _log?.Info($"{sw.Elapsed:mm':'ss'.'ff} [ES init] catching up with stream.");
            await CatchUpAsync(cancel).ConfigureAwait(false);

            _log?.Info($"{sw.Elapsed:mm':'ss'.'ff} [ES init] DONE !");
        }

        /// <summary> Catch up with locally stored data, without remote fetches. </summary>
        private void CatchUpLocal()
        {
            var caughtUpWithProjection = false;

            while (true)
            {
                TEvent nextEvent;

                try
                {
                    // This might throw due to serialization error
                    //  (but not for other reasons)
                    nextEvent = Stream.TryGetNext();
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[ES read] unreadable event at seq {Stream.Sequence}.", ex);
                    _projection.SetPossiblyInconsistent();
                    Quarantine.Add(Stream.Sequence, ex);
                    continue;
                }

                // No more local events left
                if (nextEvent == null) break;

                var seq = Stream.Sequence;

                if (_log != null && seq % 1000 == 0)
                    _log.Info($"[ES read] processing event at seq {seq}.");

                if (caughtUpWithProjection || seq > _projection.Sequence)
                {
                    caughtUpWithProjection = true;
                    try
                    {
                        // This might throw due to event processing issues
                        //  by one or more projection components
                        _projection.Apply(seq, nextEvent);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"[ES read] processing error on event at seq {seq}.", ex);
                        _projection.SetPossiblyInconsistent();
                        Quarantine.Add(seq, nextEvent, ex);
                    }
                }

                if (_onEachCommitted == null) continue;

                foreach (var e in _onEachCommitted)
                {
                    if (seq < e.Start) continue;

                    try
                    {
                        e.Listener(nextEvent, seq);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"[ES read] processing error on event at seq {seq}.", ex);
                        _projection.SetPossiblyInconsistent();
                        Quarantine.Add(seq, nextEvent, ex);
                    }

                }
            }
        }

        /// <summary>
        /// Catch up with the stream (updating the state) until there are no new 
        /// events available.
        /// </summary>
        public async Task CatchUpAsync(CancellationToken cancel = default(CancellationToken))
        {
            Func<bool> finishFetch;

            do
            {
                var fetchTask = Stream.BackgroundFetchAsync(cancel);

                // We have started fetching the next batch of events in 
                // the background, so we might as well start processing
                // those we already have. This pattern is optimized for
                // when fetching events takes longer than processing them,
                // and remains safe (i.e. no runaway memory usage) when 
                // the reverse is true.
                CatchUpLocal();

                finishFetch = await fetchTask;

            } while (finishFetch());

            // We reach this point if 1° all events cached in the stream have
            // been processed and 2° the fetch operation returned no new events

            SyncStep++;
        }

        /// <summary> Append events, constructed from the state, to the stream. </summary>
        /// <remarks> 
        /// Builder returns array of events to be appended, and additional data
        /// that will be returned by this method. Builder may be called more than
        /// once. 
        /// </remarks>
        public async Task<AppendResult<T>> AppendEventsAsync<T>(
            Func<TState, Append<TEvent, T>> builder, 
            CancellationToken cancel = default(CancellationToken))
        {
            var thrownByBuilder = false;

            try
            {
                while (true)
                {
                    thrownByBuilder = true;
                    var tuple = builder(Current);
                    thrownByBuilder = false;

                    // No events to append, just return result
                    if (tuple.Events == null || tuple.Events.Count == 0)
                        return new AppendResult<T>(0, 0, tuple.Result);

                    // Append the events                
                    var done = await Stream.WriteAsync(tuple.Events, cancel).ConfigureAwait(false);

                    if (done == null)
                    {
                        // Append failed. Catch up and try again.
                        await CatchUpAsync(cancel).ConfigureAwait(false);
                    }
                    else
                    {
                        // Append succeeded. Catch up with locally available events (including those
                        // that were just added), then return append information.
                        CatchUpLocal();
                        SyncStep++;
                        return new AppendResult<T>(tuple.Events.Count, (uint) done, tuple.Result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (!thrownByBuilder)
                    _log.Error("While appending events", e);

                throw;
            }
        }

        /// <summary> Append events, constructed from the state, to the stream. </summary>
        /// <remarks> 
        /// Builder returns array of events to be appended. It may be called more than
        /// once. 
        /// </remarks>
        public Task<AppendResult> AppendEventsAsync(
            Func<TState, Append<TEvent>> builder,
            CancellationToken cancel = default(CancellationToken))
        {
            return AppendEventsAsync(s => new Append<TEvent, bool>(builder(s), false), cancel)
                .ContinueWith(t => (AppendResult)t.Result, cancel);
        }

        /// <summary> Append events to the stream. </summary>
        /// <remarks> 
        /// This is a dangerous method, because it always adds the event to the stream
        /// regardless of state (this can lead to duplicates in multi-writer scenarios, 
        /// and so on). Make sure you know what you're doing. 
        /// </remarks>
        public Task<AppendResult> AppendEventsAsync(
            TEvent[] events,
            CancellationToken cancel = default(CancellationToken))
        {
            return AppendEventsAsync(s => new Append<TEvent, bool>(false, events), cancel)
                .ContinueWith(t => (AppendResult)t.Result, cancel);
        }

        /// <summary> Attempt to save the projection to the cache. </summary>
        /// <remarks> 
        /// While this returns a task, the save operation itself does not touch the
        /// object (only an immutable copy of the state), so you do not need to 
        /// wait for this task to finish before starting another operation.
        /// </remarks>
        public Task TrySaveAsync(CancellationToken cancel = default(CancellationToken))
        {
            return _projection.TrySaveAsync(cancel);
        }

        /// <summary> Reset the wrapper. Used when it is necessary to try again. </summary>
        public void Reset()
        {
            _projection.Reset();
            Stream.Reset();
        }

        /// <summary> 
        ///     Combines a <see cref="EventStream{TEvent}.Listener"/> and the sequence 
        ///     at which it starts listening. 
        /// </summary>
        private struct ListenerAndStart
        {
            public ListenerAndStart(EventStream<TEvent>.Listener listener, uint start)
            {
                Listener = listener;
                Start = start;
            }

            /// <summary> The listener. </summary>
            public EventStream<TEvent>.Listener Listener { get; }

            /// <summary> The first event that the listener needs to hear. </summary>
            /// <remarks> Events before this sequence are not passed. </remarks>            
            public uint Start { get; }
        }

        /// <summary> Triggered for each event committed to the stream. </summary>
        /// <remarks>
        ///     Will be triggered once for each event, in the correct order. Upon creation,
        ///     all events are processed from 
        /// </remarks>
        private List<ListenerAndStart> _onEachCommitted;

        /// <summary>
        ///     Schedules a listener to be called on each event, starting at the 
        ///     specified sequence number.
        /// </summary>
        public void OnEachCommitted(EventStream<TEvent>.Listener listener, uint seq = 0) =>
            (_onEachCommitted ?? (_onEachCommitted = new List<ListenerAndStart>()))
                .Add(new ListenerAndStart(listener, seq));
    }
}
