module Welcome

open System
open System.Threading.Tasks
open FCQRS.Workflows

// docs:workflow
// The service that sends a welcome. A call gets a key that is the same for
// every attempt of one step, so the service can recognise a repeat.
type IWelcomes =
    abstract Send: key: string * owner: string -> Task<bool>

let private policy = Retry.times 3 (TimeSpan.FromSeconds 10.)

// Welcomes a new account's owner. The input is the owner's name.
let definition (welcomes: IWelcomes) =
    Workflow.define "Welcome" (fun (owner: string) ->
        workflow {
            let! _ =
                activity "send-welcome" policy (fun attempt ->
                    welcomes.Send(attempt.IdempotencyKey, owner))
            return "welcomed"
        })
// docs:end
