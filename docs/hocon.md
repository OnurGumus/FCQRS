---
title: HOCON configuration (optional)
category: Quickstart
categoryindex: 2
index: 2
---


## HOCON configuration (optional)

**You do not need a HOCON file.** As the [quickstart](quickstart.html) shows, passing a `Connection`
to `FCQRS.Actor.api` is enough — the framework supplies the rest of the Akka.NET configuration from
built-in defaults. Reach for HOCON only when you want to override those defaults (a different
database provider, custom Akka settings, clustering). HOCON is well documented for Akka.NET; the
example below wires SQLite connection strings, and you can layer any Akka.NET settings on top.


```hocon
config {
     connection-string = "Data Source=demo.db;"
     akka {
          persistence {
               journal {
                    plugin = "akka.persistence.journal.sql"
                    sql {
                         class = "Akka.Persistence.Sql.Journal.SqlWriteJournal, Akka.Persistence.Sql"
                         connection-string = ${config.connection-string}
                         provider-name = "SQLite.MS"
                         auto-initialize = true
                    }
               }
               query.journal.sql {
                    class = "Akka.Persistence.Sql.Query.SqlReadJournalProvider, Akka.Persistence.Sql"
                    connection-string = ${config.connection-string}
                    provider-name = "SQLite.MS"
                    auto-initialize = true
               }
               snapshot-store {
                    plugin = "akka.persistence.snapshot-store.sql"
                    sql {
                         class = "Akka.Persistence.Sql.Snapshot.SqlSnapshotStore, Akka.Persistence.Sql"
                         connection-string = ${config.connection-string}
                         provider-name = "SQLite.MS"
                         auto-initialize = true
                    }
               }
          }
          extensions = ["Akka.Cluster.Tools.PublishSubscribe.DistributedPubSubExtensionProvider,Akka.Cluster.Tools"]
          stdout-loglevel = OFF
          loglevel = OFF
          log-config-on-start = false
          
          actor {
               provider = "Akka.Cluster.ClusterActorRefProvider, Akka.Cluster"
               serializers {
                    json = "Akka.Serialization.NewtonSoftJsonSerializer"
                    stj = "FCQRS.ActorSerialization+STJSerializer, FCQRS"
               }
               serialization-bindings {
                    "System.Object" = json
                    "FCQRS.Common+ISerializable, FCQRS" = stj
               }
          }
          remote {
               dot-netty.tcp {
                    public-hostname = "localhost"
                    hostname = "localhost"
                    port = 0
               }
          }
          cluster {
               distributed-data.durable.lmdb = "shard-data"
               pub-sub.send-to-dead-letters-when-no-subscribers = false
               sharding {
                    state-store-mode = ddata
                    remember-entities-store = eventsourced
               }
          }
     }
}
```