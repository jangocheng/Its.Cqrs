// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Its.Domain.Sql;
using Microsoft.Its.Domain.Sql.CommandScheduler;
using Microsoft.Its.Recipes;

namespace Microsoft.Its.Domain.Testing
{
    /// <summary>
    /// Provides methods for setting up the starting scenario for a test, including defining past events and routing domain events to handers during the test.
    /// </summary>
    public class ScenarioBuilder
    {
        internal bool prepared = false;
        private Scenario scenario;
        private readonly List<Action> beforePrepare = new List<Action>();
        internal readonly List<object> handlers = new List<object>();
        private readonly Dictionary<Type, object> commandSchedulers = new Dictionary<Type, object>();
        internal readonly List<IEvent> events = new List<IEvent>();

        private readonly FakeEventBus eventBus = new FakeEventBus();
        private long startCatchupAtEventId;
        private readonly Dictionary<Guid, object> aggregateBuilders = new Dictionary<Guid, object>();

        // a dictionary of repositories by aggregate type
        internal readonly Dictionary<Type, dynamic> repositories = new Dictionary<Type, dynamic>();
        internal readonly Dictionary<Type, Guid> defaultAggregateIdsByType = new Dictionary<Type, Guid>();
        private readonly Configuration configuration = new Configuration();

        /// <summary>
        /// Initializes a new instance of the <see cref="ScenarioBuilder"/> class.
        /// </summary>
        public ScenarioBuilder(Action<Configuration> configure = null)
        {
            configuration.Container
                         .Register<IEventBus>(c => eventBus);

            configuration.UseInMemoryEventStore()
                         .UseInMemoryCommandScheduling();

            configure?.Invoke(Configuration);
        }

        /// <summary>
        /// Helps to organize sets of events by aggregate.
        /// </summary>
        /// <typeparam name="TAggregate">The type of the aggregate.</typeparam>
        /// <param name="aggregateId">The aggregate id.</param>
        /// <returns></returns>
        public AggregateBuilder<TAggregate> For<TAggregate>(Guid aggregateId)
            where TAggregate : IEventSourced =>
                (AggregateBuilder<TAggregate>) aggregateBuilders.GetOrAdd(aggregateId, id => new AggregateBuilder<TAggregate>(id, this));

        /// <summary>
        /// Gets the domain configuration that the scenario builder uses.
        /// </summary>
        public Configuration Configuration => configuration;

        /// <summary>
        /// Instantiates aggregates from the events within the scenario, and optionally runs projection catchups through specified handlers.
        /// </summary>
        /// <returns>A Scenario based on the specifications built-up using a <see cref="ScenarioBuilder" />.</returns>
        /// <exception cref="System.InvalidOperationException">Already prepared</exception>
        public virtual Scenario Prepare()
        {
            EnsureScenarioHasNotBeenPrepared();

            beforePrepare.ForEach(@do => @do());

            prepared = true;

            // command scheduling
            if (!configuration.IsUsingInMemoryCommandScheduling())
            {
                var clockName = "TEST-" + Guid.NewGuid().ToString("N").ToETag();
                Configuration.Properties["CommandSchedulerClockName"] = clockName;
                Configuration.Container.Register<GetClockName>(c => e => clockName);
                var clockRepository = Configuration.SchedulerClockRepository();
                clockRepository.CreateClock(clockName, Clock.Now());
            }

            configuration.EnsureCommandSchedulerPipelineTrackerIsInitialized();

            scenario = new Scenario(this);

            var configurationContext = ConfigurationContext.Establish(configuration);
            configurationContext.AllowOverride = true;
            scenario.RegisterForDispose(configurationContext);
            scenario.RegisterForDispose(configuration);

            if (configuration.IsUsingSqlEventStore())
            {
                // capture the highest event id from the event store before the scenario adds new ones
                startCatchupAtEventId = configuration.EventStoreDbContext()
                                                     .DisposeAfter(db => db.HighestEventId()) + 1;
            }

            SourceAggregatesFromInitialEvents();
            SubscribeProjectors();
            RunCatchup();
            SubscribeConsequenters();
         
            InitialEvents.ForEach(ScheduleIfNeeded);

            return scenario;
        }

        /// <summary>
        /// Gets the initial events for the scenario.
        /// </summary>
        /// <remarks>Events added when saving aggregates via a repository after Prepare is called will not be added to <see cref="InitialEvents" />.</remarks>
        public IEnumerable<IEvent> InitialEvents => events.ToArray();

        /// <summary>
        /// Gets the event bus on which events are published when saved to the scenario.
        /// </summary>
        /// <value>
        /// The event bus.
        /// </value>
        public FakeEventBus EventBus => eventBus;

        internal void EnsureScenarioHasNotBeenPrepared()
        {
            if (prepared)
            {
                throw new InvalidOperationException("Once the scenario has been created by calling Prepare, you can no longer make this change.");
            }
        }

        internal void BeforePrepare(Action @do)
        {
            EnsureScenarioHasNotBeenPrepared();
            beforePrepare.Add(@do);
        }

        internal void ScheduleIfNeeded(IEvent e)
        {
            var aggregateType = e.AggregateType();

            var scheduledCommand = e as IScheduledCommand;
            if (scheduledCommand != null)
            {
                DateTimeOffset dueTime = ((dynamic) e).DueTime;
                var now = Clock.Now();
                if (dueTime > now)
                {
                    dynamic commandScheduler = GetOrAddCommandScheduler(aggregateType);
                    Task schedule = commandScheduler.Schedule((dynamic) e);
                    schedule.Wait(TimeSpan.FromSeconds(5));
                }
            }
        }

        internal object GetOrAddCommandScheduler(Type aggregateType) =>
            commandSchedulers.GetOrAdd(aggregateType, t =>
            {
                object handler = null;

                var container = configuration.Container;

                var subscriptionType = typeof (ICommandScheduler<>).MakeGenericType(t);
                handler = container.Resolve(subscriptionType);

                // add it to handlers so that it will be subscribed with the others
                handlers.Add(handler);

                return handler;
            });

        private void SourceAggregatesFromInitialEvents() =>
            InitialEvents.GroupBy(e => e.AggregateType(),
                                  e => e)
                         .ForEach(es =>
                         {
                             // populate the event stream
                             var aggregateType = es.Key;

                             var repositoryType = typeof (IEventSourcedRepository<>).MakeGenericType(aggregateType);

                             if (!configuration.IsUsingSqlEventStore())
                             {
                                 var eventStream = configuration.Container.Resolve<InMemoryEventStream>();

                                 var storableEvents = es.AssignSequenceNumbers()
                                                        .Select(e => e.ToInMemoryStoredEvent());

                                 eventStream.Append(storableEvents.ToArray())
                                            .Wait();
                             }
                             else
                             {
                                 PersistEventsToSql(es);
                             }

                             dynamic repository = Configuration.Container
                                                               .Resolve(repositoryType);

                             es.Select(e => e.AggregateId).Distinct().ForEach(id =>
                             {
                                 var aggregate = repository.GetLatest(id).Result;
                                 scenario.aggregates.Add(aggregate);
                             });
                         });

        internal IEventSourcedRepository<TAggregate> GetRepository<TAggregate>() where TAggregate : class, IEventSourced => 
            Configuration.Container.Resolve<IEventSourcedRepository<TAggregate>>();

        private void PersistEventsToSql(IEnumerable<IEvent> events)
        {
            using (var eventStore = configuration.EventStoreDbContext())
            {
                events
                    .AssignSequenceNumbers()
                    .Select(e => e.ToStorableEvent())
                    .ForEach(e => eventStore.Events.Add(e));
                eventStore.SaveChanges();
            }
        }

        private void RunCatchup()
        {
            var projectors = handlers.OrEmpty()
                                     .Where(h => h.GetType().IsProjectorType())
                                     .ToArray();

            if (!projectors.Any())
            {
                return;
            }

            if (configuration.IsUsingSqlEventStore())
            {
                var catchup = new ReadModelCatchup(
                    eventStoreDbContext: () => configuration.EventStoreDbContext(),
                    readModelDbContext: () => configuration.ReadModelDbContext(),
                    startAtEventId: startCatchupAtEventId,
                    projectors: projectors);

                using (catchup)
                using (catchup.EventBus.Errors.Subscribe(scenario.AddEventHandlingError))
                using (catchup.Progress.Subscribe(s => Console.WriteLine(s)))
                {
                    catchup.Run().Wait(TimeSpan.FromSeconds(30));
                }
            }
            else
            {
                EventBus.PublishAsync(InitialEvents.ToArray()).Wait();
            }

            if (scenario.EventHandlingErrors.Any())
            {
                throw new ScenarioSetupException(
                    "The following event handling errors occurred during projection catchup: " +
                    string.Join("\n", scenario.EventHandlingErrors.Select(e => e.Exception.ToString())));
            }
        }

        private void SubscribeProjectors() =>
            handlers
                .Where(h => h.GetType().IsProjectorType())
                .ForEach(h => EventBus.Subscribe(h));

        private void SubscribeConsequenters() =>
            handlers
                .Where(h => h.GetType().IsConsequenterType())
                .ForEach(h => EventBus.Subscribe(h));
    }
}
