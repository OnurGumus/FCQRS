open FCQRS.Model.Data
open Command
open System.Diagnostics
open Serilog
open FCQRS.Common

let isAutomated = System.Environment.GetEnvironmentVariable("AUTOMATED_TEST") <> null

let waitForKey() =
    if not isAutomated then
        System.Console.ReadKey() |> ignore


async {
    let sw = Stopwatch.StartNew()
    let timestamp() = sprintf "[%d ms]" sw.ElapsedMilliseconds

    System.IO.File.Delete "demo.db"

    printfn "%s Query started" (timestamp())
    let sub = Bootstrap.sub (Query.handleEventWrapper Bootstrap.loggerF) 0L

    let userName = "testuser"
    let password = "password"

    // --- Register User (triggers saga with verification) ---
    let registerSpan = Bootstrap.activitySource.StartActivity("RegisterUser.WithSaga") |> nonNull
    registerSpan.SetTag("userName", userName) |> ignore

    let cid1 = traceparentCid()
    let cidStr = cid1 |> ValueLens.Value |> ValueLens.Value

    Log.Information("Executing RegisterUser command for {UserName} - triggers verification saga", userName)

    use d = sub.Subscribe((fun e -> e.CID = cid1), 1)

    printfn "%s Sending register command (CID=%s)" (timestamp()) cidStr

    // Watch for scheduler task and auto-advance when it appears
    FCQRS.SchedulerController.watchForAndPauseOnNext userName
    let! result = register cid1 userName password
    FCQRS.SchedulerController.registerAutoAdvanceOnAppearance userName

    registerSpan.Dispose()
    printfn "%s Command completed" (timestamp())

    d.Task.Wait()
    printfn "%s Event received in projection (LATENCY MEASUREMENT)" (timestamp())
    printfn "%A" result

    waitForKey()

    // --- Verification ---
    let verifySpan = Bootstrap.activitySource.StartActivity("VerifyUser")  |> nonNull
    verifySpan.SetTag("userName", userName) |> ignore

    printfn "Enter verification code:"
    let code =
        if isAutomated then
            // In automated mode, just use a dummy code - the saga will handle it
            "auto-code"
        else
            System.Console.ReadLine() |> nonNull

    let! resultVerify = verify (traceparentCid()) userName code
    verifySpan.Dispose()
    printfn "%A" resultVerify

    waitForKey()

    // --- Register Duplicate (should fail) ---
    let registerFailSpan = Bootstrap.activitySource.StartActivity("RegisterUser.Duplicate") |> nonNull
    let! resultFailure = register (traceparentCid()) userName password
    registerFailSpan.Dispose()
    printfn "%A" resultFailure

    waitForKey()

    // --- Login Wrong Password ---
    let loginFailSpan = Bootstrap.activitySource.StartActivity("LoginUser.WrongPassword")  |> nonNull
    let! loginResultF = login (traceparentCid()) userName "wrong pass"
    loginFailSpan.Dispose()
    printfn "%A" loginResultF

    waitForKey()

    // --- Login Success ---
    let loginSuccessSpan = Bootstrap.activitySource.StartActivity("LoginUser.Success")  |> nonNull
    let! loginResultS = login (traceparentCid()) userName password
    loginSuccessSpan.Dispose()
    printfn "%A" loginResultS

    waitForKey()

    // Flush Serilog before exit
    Log.CloseAndFlush()
} |> Async.RunSynchronously
