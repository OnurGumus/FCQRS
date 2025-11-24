module Bootstrap
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Common
open FCQRS.Actor
open FCQRS.Model.Data
open System
open FCQRS.Scheduler
open System.Diagnostics
open Serilog
open Serilog.Extensions.Logging
open SerilogTracing

// Load scheduler configuration from hocon file
let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

// ActivitySource for distributed tracing
let activitySource = new ActivitySource("FCQRS.SagaSample")

// Configure Serilog with SerilogTracing for distributed tracing to Seq
let serilogLogger =
    LoggerConfiguration()
        .MinimumLevel.Debug()
        .Enrich.WithProperty("Application", "FCQRS.SagaSample")
        .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
        .WriteTo.Seq("http://localhost:5341")
        .CreateLogger()

Log.Logger <- serilogLogger

// Create explicit ActivityListener to ensure all activities are sampled
let _activityListener =
    let listener = new ActivityListener()
    listener.ShouldListenTo <- fun source -> true
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.SampleUsingParentId <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    // Note: Don't log in ActivityStopped - SerilogTracing handles that via TraceToSharedLogger()
    ActivitySource.AddActivityListener(listener)
    listener

// Enable SerilogTracing listener for additional trace correlation
let _tracingListener =
    ActivityListenerConfiguration()
        .TraceToSharedLogger()

// Create logger factory using Serilog
let loggerF =
    new SerilogLoggerFactory(serilogLogger) :> ILoggerFactory

// Create connection for SQLite - this will be merged with the hocon file config
let connectionString: ShortString =
    "Data Source=demo.db;" |> ValueLens.TryCreate |> Result.value

let connection =
    Some {
        ConnectionString = connectionString
        DBType = DBType.Sqlite
    }

// Create cluster name
let clusterName: ShortString =
    "cluster-system" |> ValueLens.TryCreate |> Result.value

// The api will merge the connection (default.hocon) with the existing config (scheduler)
let actorApi = FCQRS.Actor.api config loggerF connection clusterName

// Initialize the scheduler controller with the target task name
FCQRS.SchedulerController.start actorApi.System.Scheduler

let userSagaShard = UserSaga.factory actorApi
let sagaCheck (o: obj) =
    match o with
    | :? (FCQRS.Common.Event<User.Event>) as e ->
        match e.EventDetails with
        | User.VerificationRequested _ -> [ userSagaShard, id |> Some |> PrefixConversion, o ]
        | _ -> []
    | _ -> []

actorApi.InitializeSagaStarter sagaCheck

let userShard = User.factory actorApi

User.init actorApi |> ignore
UserSaga.init actorApi |> ignore

// When CID is a W3C traceparent string, distributed tracing is enabled automatically.
let userSubs cid actorId command filter metadata =
    actorApi.CreateCommandSubscription userShard cid actorId command filter metadata

let sub handleEventWrapper offsetCount =
    FCQRS.Query.init actorApi offsetCount handleEventWrapper
