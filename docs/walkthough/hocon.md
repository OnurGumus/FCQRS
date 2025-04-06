---
title: Hocon
category: Walkthrough
categoryindex: 2
index: 2
---


## Hocon file


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