module SendMail


open Akkling

type Mail =
    { To: string
      Subject: string
      Body: string }


let behavior (m: Actor<_>) =
    let rec loop () =
        actor {
            let! (mail: obj) = m.Receive()
            match mail with
            | :? Mail as mail ->
                printfn "Sending mail to %A !!" mail
                m.Sender().Tell("sent", Akka.Actor.ActorRefs.NoSender)
                return! loop ()
            | _ ->
                return! loop ()
        }

    loop ()