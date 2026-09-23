(**
---
title: Test your domain
category: Apply
categoryindex: 4
index: 8
---
*)
(*** hide ***)
#r "nuget: FCQRS, 6.6.0"
#r "nuget: Expecto, 10.2.3"
#load "../../samples/accounts/2-withdraw-money/fsharp/Account.fs"

open Expecto
open FCQRS.Common
open FCQRS.CSharp
open Account

(**
# Test your domain

Test the account from the tutorial's [withdraw money](../tutorial/withdraw-money.html) step directly,
without starting FCQRS or SQLite. `TestEnvelope` wraps your payload in the same command or event type
the runtime passes to your code.

## Check decisions, replay, and rejections

The F# assertions use Expecto. The C# tests use xUnit and the sample's `Account` class.
*)

// An account Alice opened, with a balance of 70.
let opened = { initial with Owner = Some "Alice"; Balance = 70m }

// A withdrawal the balance covers is stored.
let accepted = decide (TestEnvelope.Command(Withdraw 60m)) opened
Expect.equal accepted (PersistEvent(Withdrawn 60m)) "a covered withdrawal persists"

// Recovery applies the stored events in order, starting from the initial state.
let history =
    [ Opened "Alice"; Deposited 100m; Withdrawn 30m ]
    |> List.mapi (fun index event -> TestEnvelope.Event(event, int64 (index + 1)))
let recovered = List.fold (fun state event -> fold event state) initial history
Expect.equal recovered opened "replay restores the balance"

// A larger withdrawal is rejected, and folding the rejection changes nothing.
let rejection = Rejected "Insufficient funds: 70 available"
let overdraft = decide (TestEnvelope.Command(Withdraw 500m)) recovered
Expect.equal overdraft (DeferEvent rejection) "an overdraft is rejected"
let reply = TestEnvelope.Event(rejection, 3L)
Expect.equal (fold reply recovered) recovered "the rejection preserves state"
printfn "Account tests passed."

(**
<div class="cs-alt"></div>

```csharp
using Xunit;
using static FCQRS.CSharp;

public class AccountTests
{
    private readonly Account account = new();
    // An account Alice opened, with a balance of 70.
    private readonly AccountState opened = new("Alice", 70m);

    [Fact]
    public void Covered_withdrawal_is_persisted()
    {
        // Name the union type: the aggregate expects a Command<AccountCommand>.
        var command = TestEnvelope.Command<AccountCommand>(new Withdraw(60m));
        Assert.Equal(
            EventActions.Persist<AccountEvent>(new Withdrawn(60m)),
            account.HandleCommand(command, opened));
    }

    [Fact]
    public void Replay_restores_the_balance()
    {
        AccountEvent[] history =
            [new Opened("Alice"), new Deposited(100m), new Withdrawn(30m)];
        var state = account.InitialState;
        for (var index = 0; index < history.Length; index++)
            state = account.ApplyEvent(
                TestEnvelope.Event(history[index], index + 1), state);
        Assert.Equal(opened, state);
    }

    [Fact]
    public void Overdraft_is_rejected_and_changes_nothing()
    {
        var rejection = new Rejected("Insufficient funds: 70 available");
        var command = TestEnvelope.Command<AccountCommand>(new Withdraw(500m));
        Assert.Equal(
            EventActions.Defer<AccountEvent>(rejection),
            account.HandleCommand(command, opened));
        var reply = TestEnvelope.Event<AccountEvent>(rejection, 3);
        Assert.Equal(opened, account.ApplyEvent(reply, opened));
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
cd samples/accounts
dotnet new xunit -n Accounts.Tests --framework net11.0
dotnet add Accounts.Tests reference 2-withdraw-money/csharp/Accounts.WithdrawMoney.CSharp.csproj
```

The `global.json` in `samples/accounts` selects the .NET 11 SDK. For C#, replace
`Accounts.Tests/UnitTest1.cs` with the test class above, then run `dotnet test Accounts.Tests`. All
three tests should pass. F# prints `Account tests passed.`

As your domain grows, add cases for each command and state combination and replay complete stored
histories. Use a fixed `TimeProvider` with `TestEnvelope` when a rule depends on the envelope time.
Keep clocks and external calls out of the fold.

These tests verify the rule and replay function. Also run the application across a real restart to
check persistence and query recovery, as the [tutorial's first step](../tutorial/open-an-account.html#Run-it-again) does.
For changes to stored event shapes, [test compatibility with old events](evolve-events.html).
*)
