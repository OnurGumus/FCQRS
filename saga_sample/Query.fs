

module Query

open Microsoft.Extensions.Logging
open FCQRS.Common
open FCQRS.Model.Data
// Receives every stored event, in version order within each aggregate.
let handleEventWrapper (loggerFactory:ILoggerFactory) (event:obj)=
    let log = loggerFactory.CreateLogger "Event"
    log.LogInformation("Event: {0}", event.ToString())

    let dataEvent =
        match event with
        | :? FCQRS.Common.Event<User.Event> as  event ->
            printfn "!!Event: %A" event

            // typically do your regular insert , update ,delete for read side projection.
            // A projection with a name stores how far it has read after this handler returns,
            // so after a crash it can hand the same event again: make the update idempotent.
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
