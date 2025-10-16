module Command

open FCQRS.Model.Data
open Bootstrap

// function handles the registration of a new user.
let register cid userName password =

    // Create an actor id from the username. Using ValueLens technique to create an ActorId. See my video.
    let actorId: AggregateId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Register(userName, password)

    // Condition to wait until what we are interested in happens.
    let condition (e: User.Event) =
        e.IsAlreadyRegistered || e.IsRegisterSucceeded

    // Send the command to the actor.
    let subscribe = userSubs cid actorId command condition None

    async {
        // Wait for the event to happen andd decide the outcome.
        match! subscribe with
        | { EventDetails = User.RegisterSucceeded _
            Version = v } -> return Ok v

        | { EventDetails = User.AlreadyRegistered
            Version = _ } -> return Error [ sprintf "User %s is already registered" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for registration %s" <| actorId.ToString() ]
    }


let login cid userName password =

    let actorId: AggregateId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Login password

    let condition (e: User.Event) = e.IsLoginFailed || e.IsLoginSucceeded

    let subscribe = userSubs cid actorId command condition None

    async {
        match! subscribe with
        | { EventDetails = User.LoginSucceeded
            Version = v } -> return Ok v

        | { EventDetails = User.LoginFailed
            Version = _ } -> return Error [ sprintf "User %s Login failed" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for user %s" <| actorId.ToString() ]
    }