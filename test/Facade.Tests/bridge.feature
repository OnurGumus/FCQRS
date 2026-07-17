Feature: ExpectoTickSpec bridge
  The bridge must bind steps and load feature resources from the CALLER's
  assembly — this file and its steps live in Facade.Tests, not in the
  ExpectoTickSpec library, so this feature fails if the bridge ever falls
  back to scanning its own assembly.

  Rule: Steps bind in the caller's assembly

    Scenario: A counter round-trips through the bridge
      Given a counter starting at 40
      When 2 is added
      Then the counter shows 42
