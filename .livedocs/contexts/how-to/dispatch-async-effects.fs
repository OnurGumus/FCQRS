open System
open FCQRS.Model.Data
open FCQRS.Common
open FCQRS.FSharp
type NoteCommand = Summarize | RecordSummary of string | GiveUp
type NoteEvent = SummaryRecorded of string | SummaryUnavailable
type NoteState = { Body: string; Summary: string option }
module Note =
    let initial = { Body = "A note to summarize"; Summary = None }
let fold (event: Event<NoteEvent>) state =
    match event.EventDetails with
    | SummaryRecorded summary -> { state with Summary = Some summary }
    | SummaryUnavailable -> state
type ISummarizer =
    abstract Summarize: string -> Async<string>
// snippet: 1
let register (api: IActor) (ai: ISummarizer) =
    // snippet: 2
    notes
open Expecto
let command payload = FCQRS.CSharp.TestEnvelope.Command(payload)
let state = Note.initial
// snippet: 3
