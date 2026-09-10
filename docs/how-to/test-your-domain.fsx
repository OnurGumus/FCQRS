(**
---
title: Test your domain
category: Apply
categoryindex: 4
index: 6
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.3.1"
#r "nuget: Expecto, 10.2.3"
#load "../../samples/registration-fsharp/Account.fs"

open Expecto
open FCQRS.Common
open FCQRS.CSharp
open Account

(**
# Test your domain

Test the [registration account](../get-started.html) directly, without starting FCQRS or SQLite.
`TestEnvelope` wraps your payload in the same command or event type the runtime passes to your code.

## Check registration, replay, and repeats

The F# assertions use Expecto. The C# tests use xUnit and the sample's `Account` class.
*)

// A new account stores its first registration.
let first = decide (TestEnvelope.Command(RegisterUser "Alice")) None
Expect.equal first (PersistEvent(UserRegistered "Alice")) "first registration persists"

// Recovery starts with empty state and applies the stored event.
let stored = TestEnvelope.Event(UserRegistered "Alice", 1L)
let recovered = fold stored None
Expect.equal recovered (Some "Alice") "replay restores the name"

// A different requested name cannot overwrite the existing registration.
let repeated = decide (TestEnvelope.Command(RegisterUser "Bob")) recovered
Expect.equal repeated (DeferEvent(UserRegistered "Alice")) "repeat returns the saved name"
Expect.equal (fold stored recovered) recovered "applying the repeated reply preserves state"
printfn "Registration tests passed."

(**
<div class="cs-alt"></div>

```csharp
using Xunit;
using static FCQRS.CSharp;

public class AccountTests
{
    private readonly Account account = new();

    [Fact]
    public void First_registration_is_persisted()
    {
        var action = account.HandleCommand(
            TestEnvelope.Command(new RegisterUser("Alice")), account.InitialState);
        Assert.Equal(EventActions.Persist(new UserRegistered("Alice")), action);
    }

    [Fact]
    public void Replay_restores_the_name()
    {
        var stored = TestEnvelope.Event(new UserRegistered("Alice"), 1);
        Assert.Equal(new AccountState("Alice"), account.ApplyEvent(stored, account.InitialState));
    }

    [Fact]
    public void Repeated_registration_preserves_the_saved_name()
    {
        var state = new AccountState("Alice");
        var action = account.HandleCommand(TestEnvelope.Command(new RegisterUser("Bob")), state);
        Assert.Equal(EventActions.Defer(new UserRegistered("Alice")), action);
        var reply = TestEnvelope.Event(new UserRegistered("Alice"), 1);
        Assert.Equal(state, account.ApplyEvent(reply, state));
    }
}
```

The last assertion matters because FCQRS applies deferred replies too. They must preserve recoverable
state: a deferred change would disappear on restart.

## Run the tests

From the repository root:

```text
dotnet fsi --exec docs/how-to/test-your-domain.fsx
```

<div class="cs-alt" data-fs="text" data-cs="text"></div>

```text
dotnet new xunit -n Registration.Tests --framework net10.0
dotnet add Registration.Tests reference samples/registration-csharp/Registration.CSharp.csproj
```

For C#, replace `Registration.Tests/UnitTest1.cs` with the test class above, then run
`dotnet test Registration.Tests`. All three tests should pass. F# prints `Registration tests passed.`

As your domain grows, add cases for each command and state combination and replay complete stored
histories. Use a fixed `TimeProvider` with `TestEnvelope` when a rule depends on the envelope time.
Keep clocks and external calls out of the fold.

These tests verify the rule and replay function. Also run the application across a real restart to
check persistence and query recovery, as in the [quickstart](../get-started.html#Run-it).
For changes to stored event shapes, [test compatibility with old events](evolve-events.html).
*)
