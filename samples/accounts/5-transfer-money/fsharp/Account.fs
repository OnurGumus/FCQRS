// docs:messages
module Account

open FCQRS.Common

// What a caller, or the transfer saga, can ask an account to do.
type AccountCommand =
    | Open of owner: string
    | Deposit of amount: decimal
    | Withdraw of amount: decimal
    | SendTransfer of transferId: string * target: string * amount: decimal
    | ReceiveTransfer of transferId: string * source: string * amount: decimal
    | RefundTransfer of transferId: string * target: string * amount: decimal

// What the account replies. Rejected is a reply only: it is never stored.
type AccountEvent =
    | Opened of owner: string
    | Deposited of amount: decimal
    | Withdrawn of amount: decimal
    | TransferSent of transferId: string * target: string * amount: decimal
    | TransferReceived of transferId: string * source: string * amount: decimal
    | TransferRefunded of transferId: string * target: string * amount: decimal
    | Rejected of reason: string

// What the account knows now. The sets hold the IDs of transfers it has
// handled, so a repeated transfer command moves no money twice.
type AccountState =
    { Owner: string option
      Balance: decimal
      Sent: Set<string>
      Received: Set<string>
      Refunded: Set<string> }

// The state before the account's first event.
let initial =
    { Owner = None
      Balance = 0m
      Sent = Set.empty
      Received = Set.empty
      Refunded = Set.empty }
// docs:end

// docs:rules
// Chooses what to do with a command, based on the current state.
let decide (command: Command<AccountCommand>) (state: AccountState) =
    match command.CommandDetails, state.Owner with
    | Open _, Some _ -> DeferEvent(Rejected "The account is already open")
    | Open owner, None -> PersistEvent(Opened owner)
    | _, None -> DeferEvent(Rejected "The account is not open")
    | (Deposit amount | Withdraw amount | SendTransfer(_, _, amount)), _
        when amount <= 0m -> DeferEvent(Rejected "The amount must be positive")
    | Deposit amount, _ -> PersistEvent(Deposited amount)
    | (Withdraw amount | SendTransfer(_, _, amount)), _
        when amount > state.Balance ->
        DeferEvent(Rejected $"Insufficient funds: {state.Balance} available")
    | Withdraw amount, _ -> PersistEvent(Withdrawn amount)
    | SendTransfer(id, _, _), _ when state.Sent.Contains id ->
        DeferEvent(Rejected $"Transfer {id} was already sent")
    | SendTransfer(id, target, amount), _ ->
        PersistEvent(TransferSent(id, target, amount))
    // A repeated delivery gets the first answer; no money moves.
    | ReceiveTransfer(id, source, amount), _ when state.Received.Contains id ->
        DeferEvent(TransferReceived(id, source, amount))
    | ReceiveTransfer(id, source, amount), _ ->
        PersistEvent(TransferReceived(id, source, amount))
    | RefundTransfer(id, target, amount), _ when state.Refunded.Contains id ->
        DeferEvent(TransferRefunded(id, target, amount))
    | RefundTransfer(id, target, amount), _ ->
        PersistEvent(TransferRefunded(id, target, amount))

// Applies one event. A rejection or a repeated reply changes nothing.
let fold (event: Event<AccountEvent>) (state: AccountState) =
    match event.EventDetails with
    | Opened owner -> { state with Owner = Some owner }
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Withdrawn amount -> { state with Balance = state.Balance - amount }
    | TransferSent(id, _, amount) ->
        { state with
            Balance = state.Balance - amount
            Sent = state.Sent.Add id }
    // FCQRS folds a repeated reply too; a known ID changes nothing.
    | TransferReceived(id, _, _) when state.Received.Contains id -> state
    | TransferReceived(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Received = state.Received.Add id }
    | TransferRefunded(id, _, _) when state.Refunded.Contains id -> state
    | TransferRefunded(id, _, amount) ->
        { state with
            Balance = state.Balance + amount
            Refunded = state.Refunded.Add id }
    | Rejected _ -> state
// docs:end
