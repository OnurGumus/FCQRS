module User

open FCQRS.Common
open System

type Event =
    | LoginSucceeded
    | LoginFailed
    | AlreadyRegistered
    | VerificationRequested of string * string  * string
    | Verified

type Command =
    | Login of string
    | Verify of string
    | Register of string * string

type State =
    { Username: string option
      VerificationCode: string option
      Password: string option }

let applyEvent event state =
    match event.EventDetails with
    | VerificationRequested(userName, password, code) ->
        { state with
            VerificationCode= Some code
            Username = Some userName
            Password = Some password }
    | _ -> state

let handleCommand (cmd: Command<_>) state =
    printfn "handleCommand: %A" cmd
    match cmd.CommandDetails, state with
    | Register(userName, password), { Username = None } -> 
        VerificationRequested(userName, password, Random.Shared.Next(1_000_000).ToString()) |> PersistEvent

    | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent
    | Verify code, { VerificationCode = Some vcode } when code = vcode -> Verified |> PersistEvent
    | Login password1,
      { Username = Some _
        Password = Some password2 } when password1 = password2 -> LoginSucceeded |> PersistEvent
    | Verify _,  _ 
    | Login _, _ -> LoginFailed |> DeferEvent


let init (env: _) (actorApi: IActor) =
    let initialState = { Username = None; Password = None; VerificationCode = None }
    actorApi.InitializeActor env initialState "User" handleCommand applyEvent

let factory (env: #_) actorApi entityId =
    (init env actorApi).RefFor DEFAULT_SHARD entityId