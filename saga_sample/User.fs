module User

open FCQRS.Common
open System

type Event =
    | LoginSucceeded
    | LoginFailed
    | AlreadyRegistered
    | VerificationRequested of string * string
    | VerificationCodeSet of string
    | Verified

type Command =
    | Login of string
    | Verify of string
    | Register of string * string
    | SetVerificationCode of string

type State =
    { Username: string option
      VerificationCode: string option
      Password: string option }

let applyEvent event state =
    match event.EventDetails with
    | VerificationRequested(userName, password) ->
        { state with
            Username = Some userName
            Password = Some password }
    | VerificationCodeSet(code) ->
        { state with VerificationCode = Some code }
    | _ -> state

let handleCommand (cmd: Command<_>) state =
    printfn "handleCommand: %A" cmd
    match cmd.CommandDetails, state with
    | Register(userName, password), { Username = None } -> 
        VerificationRequested(userName, password) |> PersistEvent
    | SetVerificationCode(code), _ ->
        printfn "🎯 User received SetVerificationCode: %s" code
        VerificationCodeSet(code) |> PersistEvent

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