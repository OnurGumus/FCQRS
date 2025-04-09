module UserSaga

open FCQRS
open Common
open Common.SagaStarter
open Akkling
open SendMail


type State =
    | NotStarted
    | Started of SagaStartingEvent<Event<User.Event>>
    | SendingMail of Mail
    | Completed

type SagaData = NA

let initialState =
    { State = NotStarted
      Data = (NA: SagaData) }

let apply (sagaState: SagaState<SagaData, State>) = sagaState

let handleEvent (event: obj) (state: SagaState<SagaData, State>) = //: EventAction<State>  =
    match event, state with
    | :? string as str, _ when str = "sent" ->
        Completed |> StateChangedEvent
    | :? (Common.Event<User.Event>) as { EventDetails = userEvent }, state ->
        match userEvent, state with
        | User.VerificationRequested(email, _, code), _ ->
            SendingMail
                { To = email
                  Subject = "Your code"
                  Body = $"Your code is {code} !!" }
            |> StateChangedEvent
        | _ -> UnhandledEvent


    | _ -> UnhandledEvent

let applySideEffects
    (actorRef: unit -> IActorRef<obj>)
    env
    userFactory
    (sagaState: SagaState<SagaData, State>)
    (startingEvent: option<SagaStartingEvent<_>>)
    recovering
    =
    let originator =
        FactoryAndName
            { Factory = userFactory
              Name = Originator }

    match sagaState.State with
    | NotStarted -> NoEffect, Some(Started startingEvent.Value), []

    | Started _ ->
        if recovering then
            let startingEvent = startingEvent.Value.Event

            NoEffect,
            None,
            [ { TargetActor = originator
                Command = ContinueOrAbort startingEvent
                DelayInMs = None } ]
        else
            ResumeFirstEvent, None, []

    | SendingMail mail ->
        NoEffect,
        None,
        [ { TargetActor = ActorRef(actorRef ())
            Command = mail
            DelayInMs = None } ]

    | Completed -> StopActor, None, []

let init (env: _) (actorApi: IActor) =
    let userFactory = User.factory env actorApi

    let mailSenderRef =
        fun () -> spawnAnonymous actorApi.System (props behavior) |> retype

    actorApi.InitializeSaga
        env
        initialState
        handleEvent
        (applySideEffects mailSenderRef env userFactory)
        apply
        "UserSaga"

let factory (env: _) actorApi entityId =
    (init env actorApi).RefFor DEFAULT_SHARD entityId