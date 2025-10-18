open FCQRS.Model.Data
open Command
async{
// Start query side and get an ISubscribe to wait for the query side to catch up with the command side.
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
let! result = register cid1 userName password

// Wait for the event to happen.
d.Task.Wait()
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