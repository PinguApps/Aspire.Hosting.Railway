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

  Scenario: A cached finite build can complete without provider image digest metadata
    Given a Railway owned finite source service
    And Railway omits the built image digest
    When the finite source service completes
    Then only one source upload has run
    And its exact deployment and source snapshot remain proven

  Scenario: The publisher deadline is reported as a deployment timeout
    When runtime binding exceeds the publisher deadline
    Then a sanitized completion deadline timeout is reported

  Scenario: Caller cancellation remains cancellation
    When the caller cancels runtime binding
    Then caller cancellation is preserved

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
      | defaultBuilder |
      | defaultDockerfile |

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

  Scenario: Source publication refuses an existing custom config as code path
    Given a Railway owned source service
    And the existing service has a custom config as code path
    When the source service is rejected
    Then no source upload or provider mutation has run
    And the operator is told to clear the custom config file setting

  Scenario: Source snapshots omit local credentials and keep a stable content identity
    Given a source context containing local secrets
    When the source context is snapshotted twice
    Then the snapshot content identities match
    And local secrets are absent from the upload directory

  Scenario: Source snapshots refuse an embedded control plane credential
    Given a source context containing the control plane credential
    When the source snapshot is rejected
    Then the upload error does not disclose the credential

  Scenario: Dockerfile and Docker ignore rules are retained when excluded from build inputs
    Given a source context ignoring its nested Dockerfile and Docker ignore file
    When the nested source context is snapshotted
    Then Docker build control files are explicitly retained in the CLI upload rules

  Scenario: A nested Dockerfile does not suppress allowed siblings or restore ignored siblings
    Given a nested Dockerfile beside allowed and ignored context inputs
    When the nested source context is snapshotted
    Then the original nested sibling ignore rules remain authoritative

  Scenario: A selected Dockerfile specific ignore file retains its precedence
    Given a nested Dockerfile with its own ignore file
    When the nested source context is snapshotted
    Then the selected ignore file is staged beside the transport Dockerfile

  Scenario: Reserved transport control file collisions are rejected
    Given a nested Dockerfile with a reserved transport file collision
    When the nested source snapshot is rejected
    Then no source upload or provider mutation has run

  Scenario Outline: Railway config as code cannot override declared source deployment options
    Given a source context containing Railway config <path>
    When the source snapshot is rejected
    Then conflicting config as code is reported without its contents
    And no source upload or provider mutation has run
    Examples:
      | path                |
      | railway.json        |
      | railway.toml        |
      | nested/railway.json |


  Scenario: Source identity includes executable file modes on Unix
    Given a source context containing local secrets
    When source snapshots surround an executable mode change where supported
    Then Unix executable mode changes are copied and alter source identity

  Scenario Outline: Ambiguous source declarations fail before deployment
    Given an invalid source declaration with <conflict>
    When the source declaration is validated
    Then the declaration is rejected
    Examples:
      | conflict            |
      | retained image      |
      | escaping Dockerfile |
      | reserved argument   |
