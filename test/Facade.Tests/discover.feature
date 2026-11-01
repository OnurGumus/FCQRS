Feature: Discovered feature
  Only inspected by the discover test, under a resource prefix of its own, so
  the suite never registers it. Every pending scenario uses steps that do not
  exist, so binding any of them fails the test.

  Rule: Some scenarios run

    Scenario: Runs
      Given a counter starting at 40
      When 2 is added
      Then the counter shows 42

    @pending
    Scenario: Tagged with unwritten steps
      Given a step nobody has written yet

    Scenario: Waits in code
      Given another step nobody has written yet

    Scenario Outline: Adds <add>
      Given a counter starting at 40
      When <add> is added
      Then the counter shows <total>

      Examples:
        | add | total |
        | 2   | 42    |
        | 3   | 43    |

  Rule: Every scenario waits

    Background:
      Given a background nobody has written yet

    @pending
    Scenario: Only one here
      Given a step nobody has written yet
