open FCQRS.Model.Data
open Command
open System.Diagnostics
open Serilog

let isAutomated = System.Environment.GetEnvironmentVariable("AUTOMATED_TEST") <> null

let waitForKey() =
    if not isAutomated then
        System.Console.ReadKey() |> ignore

async{
let sw = Stopwatch.StartNew()
let timestamp() = sprintf "[%d ms]" sw.ElapsedMilliseconds

// Start query side and get an ISubscribe to wait for the query side to catch up with the command side.
printfn "%s Query started" (timestamp())
let sub = Bootstrap.sub (Query.handleEventWrapper Bootstrap.loggerF) 0L

// Helper to create a traceparent CID from current activity context for distributed tracing
// Format: "00-{traceId}-{spanId}-{flags}" (W3C traceparent)
// Each command gets its own trace - the span becomes the trace root
let traceparentCid (): CID =
    match Activity.Current with
    | null ->
        // No current activity - create plain GUID (no tracing)
        System.Guid.NewGuid().ToString() |> ValueLens.CreateAsResult |> Result.value
    | act ->
        let traceparent = $"00-{act.TraceId.ToHexString()}-{act.SpanId.ToHexString()}-01"
        traceparent |> ValueLens.CreateAsResult |> Result.value

// user name and password for testing.
let userName = "testuser"
let password = "password"

// --- Register User (separate trace) ---
// Each command creates its own root span = its own trace
let registerSpan = Bootstrap.activitySource.StartActivity("RegisterUser")
if registerSpan <> null then
    registerSpan.SetTag("userName", userName) |> ignore

let cid1 = traceparentCid()
let cidStr = cid1 |> ValueLens.Value |> ValueLens.Value

// This log will be part of the RegisterUser trace (while span is active)
Log.Information("Executing RegisterUser command for {UserName}", userName)

// Subscribe to wait for query side to catch up
use d = sub.Subscribe((fun e -> e.CID = cid1), 1)

printfn "%s Sending register command (CID=%s)" (timestamp()) cidStr
let! result = register cid1 userName password
if registerSpan <> null then registerSpan.Dispose()
printfn "%s Command completed" (timestamp())

d.Task.Wait()
printfn "%s Event received in projection (LATENCY MEASUREMENT)" (timestamp())
printfn "%A" result

// --- Register Duplicate (separate trace) ---
let registerFailSpan = Bootstrap.activitySource.StartActivity("RegisterUser.Duplicate")
let! resultFailure = register (traceparentCid()) userName password
if registerFailSpan <> null then registerFailSpan.Dispose()
printfn "%A" resultFailure

waitForKey()

// --- Login Wrong Password (separate trace) ---
let loginFailSpan = Bootstrap.activitySource.StartActivity("LoginUser.WrongPassword")
let! loginResultF = login (traceparentCid()) userName "wrong pass"
if loginFailSpan <> null then loginFailSpan.Dispose()
printfn "%A" loginResultF

// --- Login Success (separate trace) ---
let loginSuccessSpan = Bootstrap.activitySource.StartActivity("LoginUser.Success")
let! loginResultS = login (traceparentCid()) userName password
if loginSuccessSpan <> null then loginSuccessSpan.Dispose()
printfn "%A" loginResultS


// Best to wait for hit key to exit.
waitForKey()

// Flush Serilog before exit to ensure all logs are sent to Seq
Log.CloseAndFlush()
} |> Async.RunSynchronously