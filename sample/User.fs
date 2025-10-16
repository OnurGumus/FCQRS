module User

open FCQRS.Common

// Thisi is our aggregate root for the User entity.

// The state of the User aggregate is represented by the Username and Password fields.
type State =
    { Username: string option
      Password: string option }

// We can register a new user or login an existing user.
// While using strings is not the best practice, for the sake of simplicity we will use them here.
type Command =
    | Login of string
    | Register of string * string

// The outcome of the commands is represented by the following events.
// We can either succeed or fail to login, or succeed to register a new user.
type Event =
    | LoginSucceeded
    | LoginFailed
    | RegisterSucceeded of string * string
    | AlreadyRegistered

let handleCommand (cmd: Command<_>) state =
    match cmd.CommandDetails, state with
    // If we receive a Register command and the username is not already taken, we register the user.
    // PersistEvent persist the events ,applies the event and publishes the event.
    | Register(userName, password), { Username = None } ->
         RegisterSucceeded(userName, password) |> PersistEvent

    // If the username is already taken, we create an AlreadyRegistered event.
    // DeferEvent is same as PersistEvent, but it doesn't persist
    | Register _, { Username = Some _ } -> AlreadyRegistered |> DeferEvent

    // If we receive a Login command and the username and password match, we succeed to login.
    | Login password1,
        {   Username = Some _
            Password = Some password2 } when password1 = password2 -> 
                LoginSucceeded |> PersistEvent

    // If the username and password don't match, we fail to login. For this case no need to persist the event.
    | Login _, _ -> LoginFailed |> DeferEvent

// When ever an event is persisted or deferred, this function is called to apply the event to the state.
let applyEvent event state =
    match event.EventDetails with
    | RegisterSucceeded(userName, password) ->
        { state with
            Username = Some userName
            Password = Some password }
    | _ -> state


// All above functions are pure and no direct relation with akka.net. Therefore directly testable.

// This function binds the above functions to the actor. Also initiates a shard. For aggregates it is okay not to call this function directly.
let init  (actorApi: IActor) =
    let initialState = { Username = None; Password = None }
    actorApi.InitializeActor  initialState "User" handleCommand applyEvent

// This function creates an instance of the User aggregate or resumes if if it already exists based on the entityId which is a string.
// entityId is url encoded. Don't use special characters or spaces in entityId or won't work.
let factory actorApi entityId =
    (init actorApi).RefFor DEFAULT_SHARD entityId