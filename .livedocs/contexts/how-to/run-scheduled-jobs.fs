module Jobs =
    open System
    open System.Globalization
    open System.Threading.Tasks
    open FCQRS.Workflows
    // snippet: 1
    // snippet: 2
    // snippet: 3
open System
open System.Collections.Concurrent
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Actor
open FCQRS.FSharp
open FCQRS.Workflows
open Jobs
let logging = LoggerFactory.Create(fun _ -> ())
let configuration = ConfigurationBuilder().Build()
let connection = Fcqrs.connect DBType.Sqlite "Data Source=jobs.db"
let api = Fcqrs.actor configuration logging (Some connection) "jobs"
let copies = ConcurrentDictionary<string, string>()
let store =
    { new IBackupStore with
        member _.Copy(_, _) = task { return true } }
let billing =
    { new IBilling with
        member _.Issue(_, _) = task { return true } }
let waitFor (what: string) (condition: unit -> bool) = ()
// snippet: 4
// snippet: 5
// snippet: 6
// snippet: 7
