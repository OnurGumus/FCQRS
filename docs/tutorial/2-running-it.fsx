(**
---
title: Query a registered user
category: Learn FCQRS
categoryindex: 2
index: 4
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.3.1"
#load "../../samples/registration-fsharp/Account.fs"

(**
# Query a registered user

The `Query: Alice` line comes from a dictionary. This handler fills it from saved registrations:

<!-- sample: fsharp Program.fs projection -->
```fsharp
let users = ConcurrentDictionary<string, string>()
let ready = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
let project (_offset: int64) (message: obj) =
    match message with
    | :? Event<UserRegistered> as event when event.Sender = Some id ->
        let (UserRegistered name) = event.EventDetails
        users[accountId] <- name
        ready.TrySetResult() |> ignore
    | _ -> ()
```

<div class="cs-alt"></div>

<!-- sample: csharp Program.cs projection -->
```csharp
var users = new ConcurrentDictionary<string, string>();
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
void Project(long offset, object message)
{
    if (message is Event<UserRegistered> stored
        && stored.Sender?.Value.Equals(id) == true)
    {
        users[accountId] = stored.EventDetails.Name;
        ready.TrySetResult();
    }
}
```

A **projection** turns events into query data. `Sender` is the ID of the aggregate that emitted the
event; the check selects the account being queried.

The program waits for `ready.Task` before reading `users[accountId]`. Waiting only for the command reply
could read the dictionary too early. This observer is enough for the example's one immutable registration;
updates need [coordination for the particular write](../how-to/read-your-writes.html).

The dictionary disappears when the program stops. FCQRS reads the saved event from SQLite and calls
this handler to rebuild the dictionary on the next run. A 30-second wait limits this example's query
wait; a timeout does not mean registration failed.

## Connect it to FCQRS

`Program` connects the account rules and the query handler to the SQLite runtime. In F#, `api` is
created by `Fcqrs.actor`; in C#, `AddFcqrs` configures the application's host:

<!-- sample: fsharp Program.fs startup -->
```fsharp
let accounts = Fcqrs.aggregate api
                   { Name = "RegistrationFSharpAccount"; Initial = None
                     Decide = decide; Fold = fold; Snapshots = Default
                     Passivation = PassivationPolicy.Default }
Fcqrs.wireSagaStarters api []
// Register the observer before sending. Offset 0 also reads earlier registrations.
Fcqrs.projection api (Projection.single 0 project) |> ignore
```

<div class="cs-alt"></div>

<!-- sample: csharp Program.cs startup -->
```csharp
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// Register the observer before sending. Offset 0 also reads earlier registrations.
builder.Services.AddFcqrs($"Data Source={database};", "registration-csharp")
    .AddAggregate<Account>()
    .AddProjection(Project, lastOffset: 0);
using var host = builder.Build();
await host.StartAsync();
```

Register the projection before sending so it is ready to observe events. Offset `0` starts reading
at the beginning of the journal, including registrations from earlier runs. F# also requires
`wireSagaStarters api []` to complete runtime initialization even though this example has no sagas;
the C# host performs that step during startup.

`Fcqrs.aggregate` returns `accounts` in F#. In C#, `AddAggregate<Account>()` registers the
`Handler<RegisterUser, UserRegistered>` that `Program` obtains from the host.

For a durable query database, [save the data and its offset together](../how-to/add-a-projection.html).
To check the registration rule without starting FCQRS, [test your domain](../how-to/test-your-domain.html).
To call it from an HTTP client, try the optional [registration API](http-api.html).

*)

(*** hide ***)
open FCQRS.Common
open FCQRS.CSharp
open Account
let registered = fold (TestEnvelope.Event(UserRegistered "Alice", 1L)) None
assert (decide (TestEnvelope.Command(RegisterUser "Alice")) None = PersistEvent(UserRegistered "Alice"))
assert (decide (TestEnvelope.Command(RegisterUser "Bob")) registered = DeferEvent(UserRegistered "Alice"))
assert (fold (TestEnvelope.Event(UserRegistered "Alice", 1L)) registered = registered)
printfn "Registration example checked."
