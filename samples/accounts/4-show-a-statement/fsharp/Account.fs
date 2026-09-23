module Account

open FCQRS.Common

// What a caller can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal
    | Withdraw of amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    | Rejected of reason: string

// What the account knows now, rebuilt from its events.
type AccountState = { Owner: string option; Balance: decimal }

// The state before the account's first event.
let initial = { Owner = None; Balance = 0m }

// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)
    | _, None -> DeferEvent(Rejected "The account is not open")
    | (Deposit amount | Withdraw amount), _ when amount <= 0m ->
        DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | Withdraw amount, _ when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)

// Applies one event to the state. A rejection changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Withdrawn amount -> { state with Balance = state.Balance - amount }
    | Rejected _ -> state
