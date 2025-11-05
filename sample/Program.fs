open FCQRS.Model.Data
open Command
open System.Diagnostics

async{
let sw = Stopwatch.StartNew()
let timestamp() = sprintf "[%d ms]" sw.ElapsedMilliseconds

// Start query side and get an ISubscribe to wait for the query side to catch up with the command side.
printfn "%s Query started" (timestamp())
let sub = Bootstrap.sub (Query.handleEventWrapper Bootstrap.loggerF) 0L

// Helper function to create a new CID.
let cid (): CID =
    System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value

// user name and password for testing.
let userName = "testuser"

let password = "password"

let cid1 = cid()

// We create a subscription BEFORE sending the command.
// This is to ensure that we don't miss the event.
// We are interested in the first event that has the same CID as the one we are sending.
use d = sub.Subscribe((fun e -> e.CID = cid1), 1)

// Send the command to register a new user.
printfn "%s Sending register command" (timestamp())
let! result = register cid1 userName password
printfn "%s Command completed" (timestamp())

// Wait for the event to happen.
d.Task.Wait()
printfn "%s Event received in projection (LATENCY MEASUREMENT)" (timestamp())
printfn "%A" result

// Try registering the same user again.
let! resultFailure = register (cid()) userName password
printfn "%A" resultFailure

System.Console.ReadKey() |> ignore

// Login the user with incorrect password.
let! loginResultF = login (cid()) userName "wrong pass"
printfn "%A" loginResultF

// Login the user with correct password.
let! loginResultS = login (cid()) userName password 
printfn "%A" loginResultS


// Best to wait for hit key to exit.
System.Console.ReadKey() |> ignore
} |> Async.RunSynchronously