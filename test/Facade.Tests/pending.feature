@pending
Feature: Pending bridge feature
  A feature-level @pending is built from parsed source only, so its steps do
  not need to exist yet — the spec can precede the implementation.

  Rule: Unwritten steps do not fail the suite

    Scenario: A spec written before its implementation
      Given a step nobody has written yet
      Then something not yet buildable happens
