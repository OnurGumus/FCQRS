module Bootstrap

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Hocon.Extensions.Configuration
open System.IO
open FCQRS.Actor
open FCQRS.Model.Data
open Serilog
open Serilog.Extensions.Logging
open SerilogTracing
open System.Diagnostics

// ActivitySource for sample application spans - will be captured by the listener
let activitySource = new ActivitySource("FCQRS.Sample")

// Create configuration for connection-string (used by query side)
let configBuilder =
    ConfigurationBuilder()
        .AddHoconFile(Path.Combine(__SOURCE_DIRECTORY__, "config.hocon"))

let config = configBuilder.Build()

// Configure Serilog with SerilogTracing for Seq traces
let serilogLogger =
    LoggerConfiguration()
        .MinimumLevel.Debug()
        .Enrich.WithProperty("Application", "FCQRS.Sample")
        .WriteTo.Console(outputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
        .WriteTo.Seq("http://localhost:5341")
        .CreateLogger()

Log.Logger <- serilogLogger

// Create explicit ActivityListener to ensure all activities are sampled and logged
// This follows the .NET pattern for capturing activities from custom ActivitySources
let _activityListener =
    let listener = new ActivityListener()
    listener.ShouldListenTo <- fun source -> true  // Listen to all sources
    listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.SampleUsingParentId <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
    listener.ActivityStopped <- fun activity ->
        // Log completed activities to Serilog with span info
        // Note: ActivityStopped can receive null despite delegate signature
        match box activity with
        | null -> ()
        | _ ->
            Log.Information("{ActivityName} {Tags}",
                activity.OperationName,
                activity.TagObjects |> Seq.map (fun t -> $"{t.Key}={t.Value}") |> String.concat ", ")
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

// Composition root for the application environment. While being a god object is an anti-pattern, it plays nicely with F# partial application.

// Bootstrap command side with Connection parameter
let actorApi = FCQRS.Actor.api config loggerF connection clusterName

// We don't use sagas (yet) so we just return an empty list.
let sagaCheck  _ = []

// Still necessary.
actorApi.InitializeSagaStarter sagaCheck

// Create a shard for the User aggregate. A shard is like parent of aggregate actrors.
let userShard = User.factory actorApi

// Not necessary but prevents first time hit latency.
User.init actorApi |> ignore

// helper function to send commands to the actor. cid means corralation id and it is used to track the command.
// essentially it could be any string typically uuid/guid.
// When CID is a W3C traceparent string, distributed tracing is enabled automatically.
let userSubs cid actorId command filter metadata =  actorApi.CreateCommandSubscription userShard cid actorId command filter metadata

// Initializes the query side.But also gets subscription for the query side. 
// The only use case for subscription is to wait for the query side to catch up with the command side.
let sub handleEventWrapper offsetCount= FCQRS.Query.init actorApi offsetCount handleEventWrapper