module UserSaga

open FCQRS
open Common
open Common.SagaRecovery
open Akkling
open Akkling.Cluster.Sharding
open SendMail


// Only user-defined states - framework handles NotStarted/Started
type UserState =
    | GeneratingCode
    | SendingMail of Mail
    | Completed

type SagaData = NA

// Saga data
let sagaData = NA

// Handle only user events - framework now allows Started state transitions
let handleUserEvent (event: obj) (state: UserState option) : EventAction<UserState> =
    match event, state with
    | :? string as str, _ when str = "sent" ->
        Completed |> StateChangedEvent
    | :? (Common.Event<User.Event>) as { EventDetails = User.VerificationRequested(email, _) }, None ->
        // Transition from Started (no user state) to first user state
        GeneratingCode |> StateChangedEvent
    | :? (Common.Event<User.Event>) as { EventDetails = User.VerificationCodeSet(code) }, Some GeneratingCode ->
        SendingMail { To = "testuser"; Subject = "Your code"; Body = $"Your code is {code} !!" }
        |> StateChangedEvent
    | _ -> UnhandledEvent

// Handle only user side effects - no startingEvent parameter needed!
let applySideEffectsUser (userFactory: string -> IEntityRef<obj>) (mailSenderRef: unit -> IActorRef<obj>) (state: UserState) (recovering: bool) =
    match state with
    | GeneratingCode ->
        let verificationCode = System.Random.Shared.Next(100_000, 999_999).ToString()
        let command = User.SetVerificationCode(verificationCode)
        NoEffect,
        None,
        [ { TargetActor = FactoryAndName { Factory = userFactory; Name = Originator };
            Command = command;
            DelayInMs = None } ]
    | SendingMail mail ->
        NoEffect,
        None,
        [ { TargetActor = ActorRef(mailSenderRef ());
            Command = mail;
            DelayInMs = Some (10000, "testuser") } ]
    | Completed -> StopActor, None, []

// Apply function for state transformations when events are processed
let apply (sagaState: SagaState<SagaData, SagaStateWrapper<UserState, User.Event>>) = 
    // Users can modify sagaState.Data here based on events
    // For now, just return unchanged
    sagaState

let init (env: _) (actorApi: IActor) =
    let userFactory = User.factory env actorApi
    let mailSenderRef = fun () -> spawnAnonymous actorApi.System (props behavior) |> retype
    
    // One-line initialization - all wrapping handled by framework!
    initSaga<SagaData, UserState, User.Event, _>
        actorApi
        env
        sagaData
        handleUserEvent
        (applySideEffectsUser userFactory mailSenderRef)
        apply
        userFactory
        "UserSaga"

let factory (env: _) actorApi entityId =
    (init env actorApi).RefFor DEFAULT_SHARD entityId