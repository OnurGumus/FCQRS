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

    let db =
        match orderby with
        | Some orderby ->
            <@
                query {
                    for c in (%db) do
                        sortBy ((%%sortByEval orderby) c)
                        select c
                }
            @>
        | None ->
            <@
                query {
                    for c in (%db) do
                        select c
                }
            @>

    let db =
        match orderbydesc with
        | Some orderbydesc ->
            <@
                query {
                    for c in (%db) do
                        sortByDescending ((%%sortByEval orderbydesc) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match thenby with
        | Some thenby ->
            <@
                query {
                    for c in (%db) do
                        thenBy ((%%sortByEval thenby) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match thenbydesc with
        | Some thenbydesc ->
            <@
                query {
                    for c in (%db) do
                        thenByDescending ((%%sortByEval thenbydesc) c)
                        select c
                }
            @>
        | None -> db

    let db =
        match take with
        | Some take -> <@ (%db).Take(take) @>
        | None -> db

    let db =
        match skip with
        | Some skip -> <@ (%db).Skip(skip) @>
        | None -> db

    query {
        for u in (%db) do
            select u
    }

let augmentQuery filter orderby orderbydesc thenby thenbydesc (take:int option) skip db =
    augment eval  filter orderby orderbydesc thenby thenbydesc take skip db