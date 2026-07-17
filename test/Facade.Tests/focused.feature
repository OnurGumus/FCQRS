Feature: Focused bridge feature
  Only inspected for tree shape by a unit test — never registered in the
  suite, so its @focus cannot skip real tests.

    Scenario: Left alone
      Given a counter starting at 40
      When 2 is added
      Then the counter shows 42

    @focus
    Scenario: Picked out
      Given a counter starting at 40
      When 2 is added
      Then the counter shows 42
