module Account

open FCQRS.Common

// docs:account
// An account that can only be opened. Opening it stores Opened, the event
// that starts a welcome.
type AccountCommand = Open of owner: string

type AccountEvent =
    | Opened of owner: string
    // A reply only: it is never stored.
    | Rejected of reason: string

type AccountState = { Owner: string option }

let initial = { Owner = None }

let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)

let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Rejected _ -> state
// docs:end
