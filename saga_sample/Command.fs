module Command

open FCQRS.Model.Data
open Bootstrap

let register cid userName password =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Register(userName, password)

    let condition (e: User.Event) = true
       // e.IsAlreadyRegistered || e.IsVerificationRequested

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.VerificationRequested _
            Version = v } -> return Ok v

        | { EventDetails = User.AlreadyRegistered
            Version = _ } -> return Error [ sprintf "User %s is already registered" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for registration %s" <| actorId.ToString() ]
    }


let login cid userName password =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Login password

    let condition (e: User.Event) =  true // e.IsLoginFailed || e.IsLoginSucceeded

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.LoginSucceeded
            Version = v } -> return Ok v

        | { EventDetails = User.LoginFailed
            Version = _ } -> return Error [ sprintf "User %s Login failed" <| actorId.ToString() ]
        | _ -> return Error [ sprintf "Unexpected event for user %s" <| actorId.ToString() ]
    }

let verify cid userName code =

    let actorId: ActorId = userName |> ValueLens.CreateAsResult |> Result.value

    let command = User.Verify code

    let condition (e: User.Event) = true // e.IsVerified || e.IsLoginFailed

    let subscribe = userSubs cid actorId command condition

    async {
        match! subscribe with
        | { EventDetails = User.Verified
            Version = v } -> return Ok v
        | { EventDetails = User.LoginFailed
            Version = _ } -> return Error [ sprintf "User %s Login failed" <| actorId.ToString() ]

        | _ -> return Error [ sprintf "Unexpected event for user %s" <| actorId.ToString() ]
    }