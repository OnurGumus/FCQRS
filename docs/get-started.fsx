(**
---
title: Register a user
category: Learn FCQRS
categoryindex: 2
index: 2
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.3.1"
#load "../samples/registration-fsharp/Account.fs"

(**
# Register a user

Register Alice, save the event in SQLite, and query her name. Each account can register once;
later requests return the saved name. [Jump to the runnable sample](#Run-it).

## Define the messages

<!-- sample: fsharp Account.fs messages -->
```fsharp
module Account

open FCQRS.Common
open FCQRS.FSharp

type RegisterUser = RegisterUser of name: string
type UserRegistered = UserRegistered of name: string
```

<div class="cs-alt"></div>

<!-- sample: csharp Account.cs messages -->
```csharp
using static FCQRS.Common;
using static FCQRS.CSharp;

public sealed record RegisterUser(string Name);
public sealed record UserRegistered(string Name);
public sealed record AccountState(string? Name = null);
```

`RegisterUser` is a **command**: a request to register a name. `UserRegistered` is an **event**:
the recorded result. The account's **state** holds the registered name, starting with `None` in F#
or `null` in C#.

## Decide what to save

`Account.fs` / `Account.cs` contains the rule. FCQRS passes the command and current state to
`decide` / `HandleCommand`:

<!-- sample: fsharp Account.fs rules -->
```fsharp
let decide (command: Command<RegisterUser>) (state: string option) =
    let (RegisterUser name) = command.CommandDetails
    persistIf state.IsNone (UserRegistered(defaultArg state name))

let fold (event: Event<UserRegistered>) (_state: string option) =
    let (UserRegistered name) = event.EventDetails
    Some name
```

<div class="cs-alt"></div>

<!-- sample: csharp Account.cs rules -->
```csharp
public sealed class Account : Aggregate<AccountState, RegisterUser, UserRegistered>
{
    public override string EntityName => "RegistrationCSharpAccount";
    public override AccountState InitialState => new();

    public override EventAction<UserRegistered> HandleCommand(
        Command<RegisterUser> command, AccountState state) =>
        EventActions.PersistConditionally(state.Name is null,
            new UserRegistered(state.Name ?? command.CommandDetails.Name));

    public override AccountState ApplyEvent(Event<UserRegistered> stored, AccountState state) =>
        new(stored.EventDetails.Name);
}
```

- **First registration:** the name is absent, so `persistIf` / `PersistConditionally` saves the event.
- **Repeat registration:** the condition is false, so FCQRS returns a deferred reply without saving
  another event. `defaultArg state name` / `state.Name ?? command.CommandDetails.Name` keeps the existing name.
- **Apply the event:** `fold` / `ApplyEvent` copies its name into state. FCQRS also runs this during
  recovery to rebuild the account from saved events.

This state and its rules form an **aggregate**. FCQRS processes one account's commands one at a time.
`RegisterUser` and `UserRegistered` are your payloads; the `Command<T>` and `Event<T>` wrappers add
FCQRS metadata such as the request ID and event version.

## Send the request

`Program.fs` / `Program.cs` [registers the aggregate](tutorial/2-running-it.html#Connect-it-to-FCQRS)
and obtains `accounts`, the handle used to send it commands:

<!-- sample: fsharp Program.fs send -->
```fsharp
let! reply = accounts.Send (Fcqrs.newCid ()) id (RegisterUser "Alice") (fun _ -> true)
```

<div class="cs-alt"></div>

<!-- sample: csharp Program.cs send -->
```csharp
var accounts = host.Services.GetRequiredService<Handler<RegisterUser, UserRegistered>>();
var reply = await accounts(_ => true, Values.NewCID(), id, new RegisterUser("Alice"));
```

`id` selects the account `alice`. The correlation ID identifies this request; the predicate
accepts its reply. Awaiting the call gives you the aggregate's result.

The sample also builds a query view from the saved event and waits for it before printing `Query: Alice`.
[The query page](tutorial/2-running-it.html) shows that handler.

## Run it

With **.NET 10** and Git installed:

```text
git clone https://github.com/OnurGumus/FCQRS.git
cd FCQRS
```

Choose a language. The first run restores the NuGet packages.

```text
dotnet run --project samples/registration-fsharp
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet run --project samples/registration-csharp
```

```text
Registered: Alice (version 1)
Query: Alice
```

Run the same command again:

```text
Already registered: Alice (version 1)
Query: Alice
```

The stored registration survived the restart. The repeated request left the version at `1`.
The event history, called the **journal**, is in `bin/Debug/net10.0/registration.db` inside the sample folder.
This example registers a profile; it does not implement passwords or login sessions.

To start your own project, copy just `Account`, `Program`, and the project file (`.fsproj` / `.csproj`)
from your chosen sample into an empty folder, then run `dotnet run` there.

Complete source: [F#](https://github.com/OnurGumus/FCQRS/tree/main/samples/registration-fsharp) ·
[C#](https://github.com/OnurGumus/FCQRS/tree/main/samples/registration-csharp).
Next, [change the name, then the account ID](tutorial/1-the-aggregate.html).

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
