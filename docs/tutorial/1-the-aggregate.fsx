(**
---
title: Try another registration
category: Learn FCQRS
categoryindex: 2
index: 3
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.3.1"
#load "../../samples/registration-fsharp/Account.fs"

(**
# Try another registration

Run the [registration sample](../get-started.html#Run-it) once to save Alice. Then change one input.

## Ask to register Bob

In the sample's `Program.fs` / `Program.cs`, change the command to:

```fsharp
let! reply = accounts.Send (Fcqrs.newCid ()) id (RegisterUser "Bob") (fun _ -> true)
```

<div class="cs-alt"></div>

```csharp
var reply = await accounts(_ => true, Values.NewCID(), id, new RegisterUser("Bob"));
```

Keep `accountId` set to `"alice"` and run the same project again:

```text
Already registered: Alice (version 1)
Query: Alice
```

The handler uses the existing name when the account is already registered. Its conditional-persist
call returns a deferred `UserRegistered("Alice")` reply. Applying that reply leaves state unchanged,
and no second event is saved.

If the reply used `"Bob"` instead, applying it would change the in-memory name without recording the
change. Restarting would recover `"Alice"` again. That is why the
[handler](../get-started.html#Decide-what-to-save) chooses the name before deciding whether to persist.

## Create a different account

Keep the command's name as `"Bob"` and change the account ID near the top of `Program`:

```fsharp
let accountId = "bob"
```

<div class="cs-alt"></div>

```csharp
var accountId = "bob";
```

Run again:

```text
Registered: Bob (version 1)
Query: Bob
```

`alice` and `bob` identify separate aggregate instances, each with its own state and event history.
The account ID selects whose rules run; the name is the data that account stores.

Set the ID back to `"alice"` and run again: Alice is still registered at version `1`.
Restore the command's name to `"Alice"` afterward.

[See how the query works](2-running-it.html).

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
