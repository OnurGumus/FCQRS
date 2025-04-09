(**
---
title: User Registration and Login
category: Walkthrough
categoryindex: 2
index: 1
---
*)
(*** hide ***)
#r  "nuget: FCQRS, *"
#r  "nuget: Hocon.Extensions.Configuration, *"
#r  "nuget: Microsoft.Extensions.Logging.Console, *"
#r  "../../sample/bin/Debug/net9.0/sample.dll"
open System.IO
open Microsoft.Extensions.Configuration
open Hocon.Extensions.Configuration

(**


## A Walkthrough: User Registration and Login
In this example, we will create a simple user registration and login system using FCQRS. The system will consist of a `User` aggregate that handles commands for registering and logging in users.
The aggregate will emit events based on the commands it processes. We will also implement a simple actor that will manage the state of the `User` aggregate. You can find the full sample code
in the sample folder of repo.


1. **Create a new F# project:**
   dotnet new console -lang F# -n MyFCQRSApp
2. **Add the packages:** <br>
   dotnet add package FCQRS <br>
   dotnet add package Hocon.Extensions.Configuration <br>
   dotnet add package Microsoft.Extensions.Logging.Console <br>
3. **Create a hocon configuration file:** 
   [config.hocon](hocon.html)
4. **Create an Environments module and implement IConfiguration: and ILoggerFactory:**

*)

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging

type AppEnv(config: IConfiguration, loggerFactory: ILoggerFactory) =
    interface ILoggerFactory with
        member _.AddProvider(provider: ILoggerProvider) : unit = 
            loggerFactory.AddProvider provider

        member _.CreateLogger(categoryName: string) : ILogger =
            loggerFactory.CreateLogger categoryName 

        member _.Dispose() : unit = loggerFactory.Dispose()

    interface IConfiguration with
        member _.Item
            with get (key: string) = config.[key]
            and set key v = config.[key] <- v

        member _.GetChildren() = config.GetChildren()
        member _.GetReloadToken() = config.GetReloadToken()
        member _.GetSection key = config.GetSection key

(**

Above code acts as a composition root for the application environment. It wraps `IConfiguration` and `ILoggerFactory`, allowing you to manage configuration and logging in a clean and type-safe manner.
*)
