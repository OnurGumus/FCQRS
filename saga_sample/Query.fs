

module Query

open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
// All persisted events will come with monotonically increasing offset value.
let handleEventWrapper env (offsetValue: int64) (event:obj)=
    let log = (env :> ILoggerFactory).CreateLogger "Event"
    log.LogInformation("Event: {0}", event.ToString())

    let dataEvent =
        match event with
        | :? FCQRS.Common.Event<User.Event> as  event ->
            printfn "!!Event: %A" event

            // typically do your regular insert , update ,delete for read side projection.
            // then 'update' the offset value to a table or  persistent storage in the same transaction.
            // This is to ensure that if the query side crashes, it can start from the last offset value.
            // commit them atomically.
            // if you are using a database, you can use a transaction.
            // This logic can also be batched. You don't have to do it one by one.


            // Optionally return a custom event for subscribers. Typically a seperate cross bounded context event
            // or a DTO like event is preferred.
            // This is useful for subscribers that are not interested in the internal events of the system.
            // Alternatitively you can return an empty set.
            [event:> IMessageWithCID]
        |  _ -> []

    dataEvent
