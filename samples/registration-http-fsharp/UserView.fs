module UserView

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open FCQRS.Common
open Account

// Each account has one immutable registration, so one signal per account is enough.
type UserView() =
    let names = ConcurrentDictionary<string, string>()
    let signals = ConcurrentDictionary<string, TaskCompletionSource<unit>>()
    let ready id = signals.GetOrAdd(id, fun _ ->
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))

    // docs:projection
    member _.Project(_offset: int64, message: obj) =
        match message with
        | :? Event<UserRegistered> as stored ->
            match stored.Sender with
            | Some sender ->
                let id = string sender
                let (UserRegistered name) = stored.EventDetails
                names[id] <- name
                ready(id).TrySetResult() |> ignore
            | None -> ()
        | _ -> ()
    // docs:end

    member _.TryGet(id: string) = names.TryGetValue(id)

    // Also works for a repeated POST: replay or an earlier write may have signaled already.
    member _.WaitFor(id: string, cancellationToken: CancellationToken) =
        ready(id).Task.WaitAsync(TimeSpan.FromSeconds(30.), cancellationToken)
