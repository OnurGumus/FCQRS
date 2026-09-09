// docs:messages
module Account

open FCQRS.Common
open FCQRS.FSharp

type RegisterUser = RegisterUser of name: string
type UserRegistered = UserRegistered of name: string
// docs:end

// docs:rules
let decide (command: Command<RegisterUser>) (state: string option) =
    let (RegisterUser name) = command.CommandDetails
    persistIf state.IsNone (UserRegistered(defaultArg state name))

let fold (event: Event<UserRegistered>) (_state: string option) =
    let (UserRegistered name) = event.EventDetails
    Some name
// docs:end
