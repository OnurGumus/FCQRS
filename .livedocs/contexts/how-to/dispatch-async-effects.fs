open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
type NoteState = { Body: string; Summary: string option }
module Note =
    let initial = { Body = "A note to summarize"; Summary = None }
// snippet: 1
let fold (event: Event<NoteEvent>) state =
    match event.EventDetails with
    | SummaryRecorded summary -> { state with Summary = Some summary }
    | SummaryUnavailable -> state
type ISummarizer =
    abstract Summarize: string -> Async<string>
let register (api: IActor) (ai: ISummarizer) =
    // snippet: 2
    notes
open Expecto
let command payload = FCQRS.CSharp.TestEnvelope.Command(payload)
let state = Note.initial
// snippet: 3
