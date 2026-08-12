[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module internal FCQRS.AkklingHelpers

open Akka.Actor
open Akka.Cluster.Sharding
open Akkling
open Akkling.Persistence
open Akkling.Cluster.Sharding
[<AutoOpen>]
module Internal =
    type  Extractor<'Envelope, 'Message> = 'Envelope -> string * string * 'Message
    type  ShardResolver = string -> string

    type TypedMessageExtractor<'Envelope, 'Message> when 'Envelope : not null
        (extractor: Extractor<_, 'Message>, shardResolver: ShardResolver) =
        interface IMessageExtractor with
            member _.ShardId message =
                match message with
                | :? 'Envelope as env ->
                    let shardId, _, _ = extractor env
                    shardId
                | :? ShardRegion.StartEntity as e -> shardResolver (e.EntityId)
                | _ -> invalidOp <| (message .ToString() |> Unchecked.nonNull)

            member _.EntityId message =
                match message with
                | :? 'Envelope as env ->
                    let _, entityId, _ = extractor env
                    entityId
                | other -> invalidOp <| string other

            member _.EntityMessage message =
                match message with
                | :? 'Envelope as env ->
                    let _, _, msg = extractor env
                    box msg
                | other -> invalidOp <| string other
            member this.ShardId(entityId: string, _: obj): string = 
                shardResolver entityId


    // HACK over persistent actors
    type  FunPersistentShardingActor<'Message>(actor: Eventsourced<'Message> -> Effect<'Message>) as this =
        inherit FunPersistentActor<'Message>(actor)
        // sharded actors are produced in path like /user/{name}/{shardId}/{entityId}, therefore "{name}/{shardId}/{entityId}" is peristenceId of an actor
        let pid =
            this.Self.Path.Parent.Parent.Name
            + "/"
            + this.Self.Path.Parent.Name
            + "/"
            + this.Self.Path.Name

        override _.PersistenceId = pid

    // this function hacks persistent functional actors props by replacing them with dedicated sharded version using different PeristenceId strategy
    let  adjustPersistentProps (props: Props<'Message>) : Props<'Message> =
        if props.ActorType = typeof<FunPersistentActor<'Message>> then
            { props with
                ActorType = typeof<FunPersistentShardingActor<'Message>> }
        else
            props


    /// Sharding settings for one entity type. Keys under `akka.cluster.sharding.<name>`
    /// override the shared `akka.cluster.sharding` block for that type only, which is how
    /// a single aggregate gets its own `passivate-idle-entity-after` without moving the
    /// default for every other aggregate. `<name>` is the entity name, so the override key
    /// is part of the same naming contract as the journal's persistence id.
    /// Call only after `ClusterSharding.Get`, which injects the sharding reference config
    /// that supplies every key an application does not set.
    ///
    /// A registration-time `PassivationPolicy` wins over both config levels, because the
    /// aggregate's author knows its recovery cost; `Default` leaves configuration in charge.
    let shardSettingsFor
        (system: ActorSystem)
        (name: string)
        (passivation: FCQRS.Common.PassivationPolicy)
        : ClusterShardingSettings =
        let shared = system.Settings.Config.GetConfig "akka.cluster.sharding"

        let merged =
            // A non-object node here is a sharding key that happens to share the entity's
            // name (`role`, `buffer-size`, ...), not an override block: leave it alone.
            match shared.GetConfig name with
            | null -> shared
            | perType when perType.IsEmpty || not (perType.Root.IsObject()) -> shared
            | perType -> perType.WithFallback shared

        // Overlaid as HOCON rather than mutated on the settings object: Akka.NET 1.5
        // exposes PassivateIdleEntityAfter as a readonly field with no `With` builder,
        // and this keeps one resolution path for both sources.
        let withIdle (value: string) (cfg: Akka.Configuration.Config) =
            Akka.Configuration.ConfigurationFactory
                .ParseString(sprintf "passivate-idle-entity-after = %s" value)
                .WithFallback cfg

        let resolved =
            match passivation with
            | FCQRS.Common.PassivationPolicy.Default -> merged
            // 0 is Akka's own "disabled" value: ShouldPassivateIdleEntities is false at
            // or below zero, so a non-positive After collapses into Never rather than
            // passivating instantly.
            | FCQRS.Common.PassivationPolicy.Never -> withIdle "0" merged
            | FCQRS.Common.PassivationPolicy.After idle when idle <= System.TimeSpan.Zero -> withIdle "0" merged
            | FCQRS.Common.PassivationPolicy.After idle ->
                withIdle (sprintf "%dms" (int64 idle.TotalMilliseconds)) merged

        // Resolved the same way Akka resolves it from the shared block, so a per-type
        // `coordinator-singleton` path is honoured too.
        let singletonPath = resolved.GetString("coordinator-singleton", "akka.cluster.singleton")
        ClusterShardingSettings.Create(resolved, system.Settings.Config.GetConfig singletonPath)

    let entityFactoryFor
        (system: ActorSystem)
        (shardResolver: ShardResolver)
        (name: string)
        (props: Props<'Message>)
        (passivation: FCQRS.Common.PassivationPolicy)
        rememberEntities
        : EntityFac<'Message> =

        let clusterSharding = ClusterSharding.Get(system)
        let adjustedProps = adjustPersistentProps props
        let settings = shardSettingsFor system name passivation

        let shardSettings =
            match rememberEntities with
            // Remembered entities disable idle passivation in Akka.NET regardless of the
            // timeout, which is why sagas pass Default and never carry a policy.
            | true -> settings.WithRememberEntities(true)
            | _ -> settings

        let shardRegion =
            clusterSharding.Start(
                name,
                adjustedProps.ToProps(),
                shardSettings,
                new TypedMessageExtractor<_, _>(EntityRefs.entityRefExtractor, shardResolver)
            )

        { 
            ShardRegion = shardRegion
            TypeName = name }

    let (|Recovering|_|) (context: Eventsourced<'Message>) (msg: 'Message) : 'Message option =
        if context.IsRecovering() then Some msg else None