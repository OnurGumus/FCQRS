// docs:messages
module Account

open FCQRS.Common

// What a caller can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal

// What the account records when it accepts a command.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal

// What the account knows now, rebuilt from its events.
type AccountState = { Owner: string option; Balance: decimal }

// The state before the account's first event.
let initial = { Owner = None; Balance = 0m }
// docs:end

// docs:rules
// Chooses what to do with a command: here, always store an event.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails with
    | Open owner -> PersistEvent(Opened owner)
    | Deposit amount -> PersistEvent(Deposited amount)

// Applies one stored event to the state.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
// docs:end
