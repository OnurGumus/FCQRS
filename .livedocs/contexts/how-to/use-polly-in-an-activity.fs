module Price =
    open System
    open System.Threading
    open System.Threading.Tasks
    open Polly
    open Polly.CircuitBreaker
    open Polly.RateLimiting
    open FCQRS.Common
    open FCQRS.Workflows
    type IPriceService =
        abstract Fetch: key: string * symbol: string * cancel: CancellationToken -> Task<decimal>
    // snippet: 1
    // snippet: 2
    // snippet: 3
