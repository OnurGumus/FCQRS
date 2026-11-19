---
title: Start a workflow from an aggregate event
category: Apply
categoryindex: 5
index: 12
---

# Start a workflow from an aggregate event

Most workflows start from a request: the application calls `Start` with an ID it chose, as
[Transfer money](../tutorial/transfer-money.html) does. Some work follows from an event that no single
request asks for, such as welcoming the owner of every account that opens, whichever code path
opened it. This recipe starts a `Welcome` workflow for each `Opened` event an account stores, from a
projection.

The complete programs are in `samples/start-from-an-event/fsharp` and
`samples/start-from-an-event/csharp`.

## The account and the workflow

The account stores `Opened` once, and rejects a second `Open` without storing anything:

<!-- sample: start-from-an-event/fsharp Account.fs account -->
```fsharp
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
```

<div class="cs-alt"></div>

<!-- sample: start-from-an-event/csharp Account.cs account -->
```csharp
// An account that can only be opened. Opening it stores Opened, the event
// that starts a welcome.
public sealed record Open(string Owner);

// The case names are stored with each event, so they must not change.
[JsonDerivedType(typeof(Opened), "Opened")]
[JsonDerivedType(typeof(Rejected), "Rejected")]
public abstract record AccountEvent;
public sealed record Opened(string Owner) : AccountEvent;
// A reply only: it is never stored.
public sealed record Rejected(string Reason) : AccountEvent;

public sealed record AccountState(string? Owner);

public sealed class Account : Aggregate<AccountState, Open, AccountEvent>
{
    public override string EntityName => "Account";
    public override AccountState InitialState => new((string?)null);

    public override EventAction<AccountEvent> HandleCommand(
        Command<Open> command, AccountState state) =>
        state.Owner is null
            ? EventActions.Persist<AccountEvent>(
                new Opened(command.CommandDetails.Owner))
            : EventActions.Defer<AccountEvent>(
                new Rejected("The account is already open"));

    public override AccountState ApplyEvent(
        Event<AccountEvent> stored, AccountState state) =>
        stored.EventDetails is Opened opened ? new(opened.Owner) : state;
}
```

The workflow sends one welcome through an external service, with the step's idempotency key:

<!-- sample: start-from-an-event/fsharp Welcome.fs workflow -->
```fsharp
// The service that sends a welcome. A call gets a key that is the same for
// every attempt of one step, so the service can recognise a repeat.
type IWelcomes =
    abstract Send: key: string * owner: string -> Task<bool>

let private policy = Retry.times 3 (TimeSpan.FromSeconds 10.)

// Welcomes a new account's owner. The input is the owner's name.
let definition (welcomes: IWelcomes) =
    Workflow.define "Welcome" (fun (owner: string) ->
        workflow {
            let! _ =
                activity "send-welcome" policy (fun attempt ->
                    welcomes.Send(attempt.IdempotencyKey, owner))
            return "welcomed"
        })
```

<div class="cs-alt"></div>

<!-- sample: start-from-an-event/csharp Welcome.cs workflow -->
```csharp
// The service that sends a welcome. A call gets a key that is the same for
// every attempt of one step, so the service can recognise a repeat.
public interface IWelcomes
{
    Task<bool> Send(string key, string owner);
}

// Welcomes a new account's owner. The input is the owner's name.
public sealed class WelcomeWorkflow(IWelcomes welcomes)
    : Workflow<string, string>("Welcome")
{
    private static readonly RetryPolicy Policy =
        Retry.times(3, TimeSpan.FromSeconds(10));

    public override async WorkflowTask<string> Run(
        WorkflowContext ctx, string owner)
    {
        await ctx.Activity("send-welcome", Policy,
            attempt => welcomes.Send(attempt.IdempotencyKey, owner));
        return "welcomed";
    }
}
```

## Start the workflow from a projection

<!-- sample: start-from-an-event/fsharp Program.fs projection -->
```fsharp
// Starts a welcome for each stored Opened event. The workflow ID comes from
// the event, so a start for an event the projection hands over again stores
// nothing. The projection stores how far it has read after the handler
// returns, so a crash before that hands the event over again.
let startWelcome (event: obj) =
    match event with
    | :? Event<AccountEvent> as stored ->
        match stored.EventDetails with
        | Opened owner ->
            let id = $"welcome-{stored.Sender.Value}"
            welcomes.Start id owner |> Async.RunSynchronously |> ignore
        | _ -> ()
    | _ -> ()

let starter =
    Projection.single (Named "welcome-starter") startWelcome
    |> Projection.ofEntities [ "Account" ]
    |> Fcqrs.projection api
```

<div class="cs-alt"></div>

<!-- sample: start-from-an-event/csharp Program.cs projection -->
```csharp
// Starts a welcome for each stored Opened event. The workflow ID comes from
// the event, so a start for an event the projection hands over again stores
// nothing. The projection stores how far it has read after the handler
// returns, so a crash before that hands the event over again.
builder.Services.AddFcqrs($"Data Source={database}", "accounts")
    .AddAggregate<Account>()
    .AddProjection(services =>
    {
        var welcomes = services
            .GetRequiredService<IWorkflowClient<string, string>>();
        return (Action<object>)(message =>
        {
            if (message is Event<AccountEvent>
                {
                    EventDetails: Opened opened, Sender: { } sender
                })
            {
                var id = $"welcome-{sender.Value}";
                welcomes.StartAsync(id, opened.Owner).GetAwaiter().GetResult();
            }
        });
    }, name: "welcome-starter", entities: ["Account"]);
```

The projection reads the account's journal and calls `Start` for each `Opened`. Three choices make
every stored event start its workflow once:

- **The workflow ID comes from the event.** `welcome-alice` names the account that stored `Opened`.
  A second start with the same ID stores nothing, so a handler that runs twice for one event starts
  one workflow. For an event that an aggregate can store more than once, add its version to the ID.
- **The projection stores its progress.** With a name, it keeps how far it has read in the journal's
  database and stores the position after the handler returns. A process that stops before that hands
  the same event to the handler again after the restart, and the start stores nothing the second
  time. A projection without a name rereads the whole journal at every start.
- **The handler waits for the start.** `Start` returns once the workflow has stored its start, so the
  projection moves past an event only after its workflow exists.

Only stored events reach a projection. A rejected `Open` is a reply, not an event, so it starts
nothing.

## Run it

From the repository root:

```text
dotnet run --project samples/start-from-an-event/fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/start-from-an-event/csharp
```

The first run prints:

```text
Opened for Alice (version 1, stored)
Sending a welcome to Alice
welcome-alice: welcomed
```

A second run finds Alice's account open, and the projection resumes after the event it handled, so no
welcome is sent again:

```text
Rejected: The account is already open (version 1, not stored)
welcome-alice: welcomed
```

## What the projection does not do

- **It starts the workflow after the event, not with it.** A projection follows the journal, so the
  start comes one poll interval, a second by default, after the event is stored. A workflow that must
  exist before anything else happens belongs at the request: start it from the code that sends the
  command.
- **A start that fails stops the process.** A handler that raises terminates the process, as
  [Add a projection](add-a-projection.html#Handle-failures-visibly) describes, and the restarted
  projection hands the same event over again. `Start` raises `TimeoutException` when the workflow
  does not answer within 30 seconds.
- **A transactional projection cannot start workflows on SQLite.** Its handler runs while the
  transaction holds the database's single writer lock, and the workflow's start is a journal write
  that waits for that lock. Use a named projection, as here, which stores its position after the
  handler returns.
- **It does not replace the workflow's idempotency.** The welcome service still sees each attempt's
  key, and must recognise a repeat: the activity runs at least once.

Next: [Run scheduled jobs](run-scheduled-jobs.html) starts workflows nightly, weekly, or at an
interval.
