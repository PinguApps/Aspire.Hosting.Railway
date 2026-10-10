Feature: Railway owned Dockerfile builds
  Source uploads remain explicit while retained registry images keep their existing default.

  Scenario: A source build deploys once and retains its exact Railway image
    Given a Railway owned source service
    When the source service is published twice
    Then only one source upload has run
    And the configuration was committed without triggering a registry deployment
    And the completed deployment exposes its Railway image digest

  Scenario: An accepted finite source upload is recovered without executing twice
    Given a Railway owned finite source service
    When an accepted upload response is lost and publication is resumed
    Then only one source upload has run
    And the completed deployment exposes its Railway image digest

  Scenario: Changed source content starts a new Railway build
    Given a Railway owned source service
    When the source content changes after successful publication
    Then two source uploads have run
    And the completed deployment exposes its Railway image digest

  Scenario: Another upload's metadata cannot satisfy a source request
    Given a Railway owned finite source service
    And the upload metadata belongs to another request
    When upload correlation is rejected and publication is resumed
    Then only one source upload has run
    And the sent upload remains recorded for recovery

  Scenario Outline: Source proof metadata can arrive after the deployment ID
    Given a Railway owned source service
    And source proof <field> is temporarily absent
    When the source service is published twice
    Then only one source upload has run
    And the completed deployment exposes its Railway image digest
    Examples:
      | field      |
      | cliMessage |
      | builder    |
      | dockerfile |

  Scenario: Source upload refuses another environment's scoped credential
    Given a Railway owned source service
    And the credential belongs to another environment
    When the source service is rejected
    Then no source upload or provider mutation has run

  Scenario: Source upload refuses an unowned existing service
    Given a Railway owned source service
    And an unowned Railway service already exists
    When the source service is rejected
    Then no source upload or provider mutation has run

  Scenario: Source snapshots omit local credentials and keep a stable content identity
    Given a source context containing local secrets
    When the source context is snapshotted twice
    Then the snapshot content identities match
    And local secrets are absent from the upload directory

  Scenario: Source snapshots refuse an embedded control plane credential
    Given a source context containing the control plane credential
    When the source snapshot is rejected
    Then the upload error does not disclose the credential

  Scenario Outline: Ambiguous source declarations fail before deployment
    Given an invalid source declaration with <conflict>
    When the source declaration is validated
    Then the declaration is rejected
    Examples:
      | conflict            |
      | retained image      |
      | escaping Dockerfile |
      | reserved argument   |
