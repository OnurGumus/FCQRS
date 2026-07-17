/// Steps for bridge.feature — deliberately defined HERE, in the consumer
/// assembly, so the ExpectoTickSpec bridge is proven to discover steps in the
/// assembly it is handed rather than its own.
module BridgeSteps

open TickSpec

[<Given>]
let ``a counter starting at (\d+)`` (n: int) = n

[<When>]
let ``(\d+) is added`` (add: int) (counter: int) = counter + add

[<Then>]
let ``the counter shows (\d+)`` (expected: int) (counter: int) =
    if counter <> expected then
        failwithf "expected %d, got %d" expected counter
