module QuoteTests

open System
open Expecto
open FCQRS.Workflows
open Quote

// docs:setup
// No activity runs in a test: the test gives each step's outcome. These
// stand-ins fail the test if one runs anyway.
let private prices = {
    new IPriceService with
        member _.Fetch(_, _) = failwith "a test runs no activity"
}

let private mailer = {
    new IMailer with
        member _.Send(_, _, _) = failwith "a test runs no activity"
}

let private quote = definition prices mailer
let private large = { Customer = "Ada"; Symbol = "ABC"; Quantity = 100 }
// docs:end

// docs:approved
let approved =
    test "a quote over 1000 is sent once a person approves it" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        let waits = flow.Status.IsWaitingFor "approval"
        Expect.isTrue waits "1250 needs approval"
        flow.Signal("approval", true)
        flow.Complete("send-quote", "receipt-1")
        let expected = { Total = 1250m; Approved = true; Receipt = "receipt-1" }
        Expect.equal flow.Result expected "the quote is sent"
        // A restart after any stored record must store the same journal.
        flow.CheckReplay()
    }
// docs:end

// docs:declined
let declined =
    test "a quote nobody approves within a day is declined" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        flow.Advance(TimeSpan.FromDays 1.0)
        let expected = { Total = 1250m; Approved = false; Receipt = "" }
        Expect.equal flow.Result expected "the wait timed out"
        flow.CheckReplay()
    }
// docs:end

// docs:failed
let mailerDown =
    test "a mailer that stays down faults the quote" {
        let flow = WorkflowTest.Start(quote, { large with Quantity = 10 })
        flow.Complete("price", 12.5m)
        // As if the retry policy's five attempts had failed.
        flow.Fail("send-quote", TimeoutException "the mailer is down")
        match flow.Status with
        | WorkflowStatus.Failed error ->
            Expect.equal error.Kind ErrorKind.Exhausted "the attempts ran out"
        | other -> failtestf "expected Failed, found %A" other
    }
// docs:end

// docs:journal
let journal =
    test "a quote stores the step names that running quotes depend on" {
        let flow = WorkflowTest.Start(quote, large)
        flow.Complete("price", 12.5m)
        flow.Signal("approval", true)
        flow.Complete("send-quote", "receipt-1")
        let expected = [
            "started"
            "scheduled 0 price"; "completed 0 price"
            "scheduled 1 approval"; "signal approval"; "completed 1 approval"
            "scheduled 2 send-quote"; "completed 2 send-quote"
            "finished"
        ]
        Expect.equal (List.ofSeq flow.Summary) expected "the stored steps"
    }
// docs:end

[<EntryPoint>]
let main argv =
    testList "quote" [ approved; declined; mailerDown; journal ]
    |> runTestsWithCLIArgs [] argv
