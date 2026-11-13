// docs:messages
module Account

open FCQRS.Common

// What the program and the order workflow can ask an account to do.
type AccountCommand =
    | Deposit of amount: decimal
    | Debit of payment: string * amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Deposited of amount: decimal
    | Debited of payment: string * amount: decimal
    | Rejected of reason: string

// What the account knows now. Payments holds the payments it has taken, so
// a repeated debit takes the money once.
type AccountState = { Balance: decimal; Payments: Set<string> }

// The state before the account's first event.
let initial = { Balance = 0m; Payments = Set.empty }
// docs:end

// docs:rules
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails with
    | Deposit amount -> PersistEvent(Deposited amount)
    // A repeated debit gets the first answer. It is checked before the
    // balance, because the first debit has already lowered the balance.
    | Debit(payment, amount) when state.Payments.Contains payment ->
        DeferEvent(Debited(payment, amount))
    | Debit(_, amount) when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Debit(payment, amount) -> PersistEvent(Debited(payment, amount))

// Applies one event. A rejection or a repeated reply changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Debited(payment, _) when state.Payments.Contains payment -> state
    | Debited(payment, amount) ->
        { state with
            Balance = state.Balance - amount
            Payments = state.Payments.Add payment
        }
    | Rejected _ -> state
// docs:end
