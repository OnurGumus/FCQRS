module FCQRS.SQLProvider.Query
open FSharp.Data.Sql.Common
open System.Linq
open FCQRS.Model.Data
let private sortByEval column =
    <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn<System.IComparable>(column) @@>


let rec private eval t =
        match t with
        | Equal(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) = n @@>
        | NotEqual(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) <> n @@>
        | Greater(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) > n @@>
        | GreaterOrEqual(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) >= n @@>
        | Smaller(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) < n @@>
        | SmallerOrEqual(s, n) -> <@@ fun (x: SqlEntity) -> (x:IColumnHolder).GetColumn(s) <= n @@>
        | And(t1, t2) -> <@@ fun (x: SqlEntity) -> (%%eval t1) x && (%%eval t2) x @@>
        | Or(t1, t2) -> <@@ fun (x: SqlEntity) -> (%%eval t1) x || (%%eval t2) x @@>
        | Not(t0) -> <@@ fun (x: SqlEntity) -> not ((%%eval t0) x) @@>

let private augment  eval filter orderby orderbydesc thenby thenbydesc (take:int option) skip db =
    let db =
        match filter with
        | Some filter ->
            <@
                query {
                    for c in (%db) do
                        where ((%%eval filter) c)
                        select c
                }
            @>
        | None -> db

    // A secondary sort must live in the SAME query expression as the primary:
    // F# translates a second sort clause there into ThenBy. A spliced previous
    // query is statically IQueryable even when it ends in sortBy, and the
    // expression builder rejects ThenBy over that (ArgumentException) — the
    // shape thenby/thenbydesc used to have, so every secondary sort crashed.
    // When both orderby and orderbydesc are set the descending one wins
    // (sequential OrderBy calls reset rather than chain — long-standing
    // behavior); the same precedence applies to thenbydesc over thenby.
    let db =
        match orderbydesc, orderby, thenbydesc, thenby with
        | Some obd, _, Some tbd, _ ->
            <@
                query {
                    for c in (%db) do
                        sortByDescending ((%%sortByEval obd) c)
                        thenByDescending ((%%sortByEval tbd) c)
                        select c
                }
            @>
        | Some obd, _, None, Some tb ->
            <@
                query {
                    for c in (%db) do
                        sortByDescending ((%%sortByEval obd) c)
                        thenBy ((%%sortByEval tb) c)
                        select c
                }
            @>
        | Some obd, _, None, None ->
            <@
                query {
                    for c in (%db) do
                        sortByDescending ((%%sortByEval obd) c)
                        select c
                }
            @>
        | None, Some ob, Some tbd, _ ->
            <@
                query {
                    for c in (%db) do
                        sortBy ((%%sortByEval ob) c)
                        thenByDescending ((%%sortByEval tbd) c)
                        select c
                }
            @>
        | None, Some ob, None, Some tb ->
            <@
                query {
                    for c in (%db) do
                        sortBy ((%%sortByEval ob) c)
                        thenBy ((%%sortByEval tb) c)
                        select c
                }
            @>
        | None, Some ob, None, None ->
            <@
                query {
                    for c in (%db) do
                        sortBy ((%%sortByEval ob) c)
                        select c
                }
            @>
        | None, None, Some _, _
        | None, None, None, Some _ ->
            invalidArg
                "thenby"
                "thenby/thenbydesc require orderby or orderbydesc: a secondary sort needs a primary sort to chain onto."
        | None, None, None, None -> db

    // Skip BEFORE Take: SQL OFFSET/FETCH and LINQ both paginate skip-then-take.
    // Take(n).Skip(m) would return items m+1..n of the first n rows (and nothing
    // when m >= n) — the inverted composition this used to have.
    let db =
        match skip with
        | Some skip -> <@ (%db).Skip(skip) @>
        | None -> db

    let db =
        match take with
        | Some take -> <@ (%db).Take(take) @>
        | None -> db

    query {
        for u in (%db) do
            select u
    }

let augmentQuery filter orderby orderbydesc thenby thenbydesc (take:int option) skip db =
    augment eval  filter orderby orderbydesc thenby thenbydesc take skip db