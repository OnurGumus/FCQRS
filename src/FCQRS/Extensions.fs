namespace FCQRS

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open FCQRS.Model.Data

/// Top-level Async->Task conversion, discoverable from C# as `someAsync.AsTask()`
/// (`using FCQRS;`). Replaces the non-discoverable FCQRS.CSharp.AsyncExtensions.
[<Extension>]
type AsyncTaskExtensions =
    /// Run an F# Async as a Task (Async.StartAsTask).
    [<Extension>]
    static member AsTask<'T>(computation: Async<'T>) : Task<'T> =
        Async.StartAsTask computation

/// Top-level C# extension methods for ISubscribe. These live at *namespace*
/// level (not nested inside the FCQRS.CSharp module) so C# actually discovers
/// them as extensions — consumers `using FCQRS;` and write
/// `subs.SubscribeFor(cid, n)` / `subs.SubscribeForFirst(cid)`.
[<Extension>]
type SubscribeExtensions =

    /// Subscribe for `take` events matching the correlation id.
    [<Extension>]
    static member SubscribeFor<'T when 'T :> IMessageWithCID>(subs: Query.ISubscribe<'T>, cid: CID, take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, take)

    /// Subscribe for `take` events matching the correlation id and a filter.
    [<Extension>]
    static member SubscribeFor<'T when 'T :> IMessageWithCID>(subs: Query.ISubscribe<'T>, cid: CID, filter: Func<'T, bool>, take: int) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, (fun e -> filter.Invoke(e)), take)

    /// Subscribe for `take` events matching a filter.
    [<Extension>]
    static member SubscribeFor<'T when 'T :> IMessageWithCID>(subs: Query.ISubscribe<'T>, filter: Func<'T, bool>, take: int) : Query.IAwaitableDisposable =
        subs.Subscribe((fun e -> filter.Invoke(e)), take)

    /// Subscribe for the first event matching the correlation id (take = 1).
    [<Extension>]
    static member SubscribeForFirst<'T when 'T :> IMessageWithCID>(subs: Query.ISubscribe<'T>, cid: CID) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, 1)

    /// Subscribe for the first event matching the correlation id and a filter.
    [<Extension>]
    static member SubscribeForFirst<'T when 'T :> IMessageWithCID>(subs: Query.ISubscribe<'T>, cid: CID, filter: Func<'T, bool>) : Query.IAwaitableDisposable =
        subs.Subscribe(cid, (fun e -> filter.Invoke(e)), 1)
