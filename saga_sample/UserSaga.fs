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

type SagaData = {
    RetryCount: int
}

// Saga data
let sagaData = { RetryCount = 0 }

// Handle only user events - framework now allows Started state transitions  
let handleUserEvent (event: obj) (sagaState: SagaState<SagaData, UserState option>) : EventAction<UserState> =
    match event, sagaState.State with
    | :? string as str, _ when str = "sent" ->
        Completed |> StateChangedEvent
    | :? (Common.Event<User.Event>) as { EventDetails = User.VerificationRequested(email, userId) }, None ->
        // Store user info in apply function, transition to GeneratingCode here
        GeneratingCode |> StateChangedEvent
    | :? (Common.Event<User.Event>) as { EventDetails = User.VerificationCodeSet(code) }, Some GeneratingCode ->
        SendingMail { To = "user@example.com"; Subject = "Your code"; Body = $"Your code is {code} !!" }
        |> StateChangedEvent
    | _ -> UnhandledEvent

// Handle only user side effects - no startingEvent parameter needed!
let applySideEffectsUser (userFactory: string -> IEntityRef<obj>) (mailSenderRef: unit -> IActorRef<obj>) (sagaState: SagaState<SagaData, UserState>) (recovering: bool) =
    match sagaState.State with
    | GeneratingCode ->
        let verificationCode = System.Random.Shared.Next(100_000, 999_999).ToString()
        let command = User.SetVerificationCode(verificationCode)
        Stay,
        [ { TargetActor = FactoryAndName { Factory = userFactory; Name = Originator };
            Command = command;
            DelayInMs = None } ]
    | SendingMail mail ->
        if sagaState.Data.RetryCount > 3 then
            StopSaga, []
        else
            Stay,
            [ { TargetActor = ActorRef(mailSenderRef ());
                Command = mail;
                DelayInMs = Some (10000, "testuser") } ]
    | Completed -> StopSaga, []

// Apply function for state transformations when events are processed
let apply (sagaState: SagaState<SagaData, SagaStateWrapper<UserState, User.Event>>) = 
    // Update cross-cutting data based on current state
    match sagaState.State with
    | UserDefined (SendingMail _) ->
        // Each time we're in SendingMail state, increment retry count
        { sagaState with Data = { sagaState.Data with RetryCount = sagaState.Data.RetryCount + 1 } }
    | UserDefined GeneratingCode ->
        // Reset retry count when starting fresh
        { sagaState with Data = { sagaState.Data with RetryCount = 0 } }
    | _ -> sagaState

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