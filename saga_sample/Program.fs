open FCQRS.Model.Data
open Command
open System.Threading.Tasks

let sub = Bootstrap.sub Query.handleEventWrapper 0L

let cid (): CID =
    System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value

let userName = "testuser"

let password = "password"

let cid1 = cid()

let s = sub.Subscribe((fun e -> e.CID = cid1), 1)
System.Console.ReadKey() |> ignore

// Start the operation asynchronously
FCQRS.SchedulerController.watchForAndPauseOnNext "testuser"
let registerTask = register cid1 userName password |> Async.StartAsTask
//System.Threading.Thread.Sleep(1000)
FCQRS.SchedulerController.registerAutoAdvanceOnAppearance "testuser"

// Advance time while the task is running
//Bootstrap.advanceTime 100 |> ignore
//Bootstrap.advanceTime 10000 |> ignore
// Wait for the result
let result = registerTask.Result
s.Task.Wait()
s.Dispose()
//Bootstrap.advanceTime 100 |> ignore
printfn "%A" result

let code = System.Console.ReadLine() |> nonNull

// Verification
let verifyTask = verify (cid()) userName code |> Async.StartAsTask
//Bootstrap.advanceTime 100 |> ignore
let resultVerify = verifyTask.Result
printfn "%A" resultVerify

System.Console.ReadKey() |> ignore

// Register failure case
let failureTask = register (cid()) userName password |> Async.StartAsTask
//Bootstrap.advanceTime 100 |> ignore
let resultFailure = failureTask.Result
printfn "%A" resultFailure

System.Console.ReadKey() |> ignore

// Login failure
let loginFailTask = login (cid()) userName "wrong pass" |> Async.StartAsTask
//Bootstrap.advanceTime 100 |> ignore
let loginResultF = loginFailTask.Result
printfn "%A" loginResultF

System.Console.ReadKey() |> ignore

// Login success
let loginSuccessTask = login (cid()) userName password |> Async.StartAsTask
//Bootstrap.advanceTime 100 |> ignore
let loginResultS = loginSuccessTask.Result
printfn "%A" loginResultS

System.Console.ReadKey() |> ignore