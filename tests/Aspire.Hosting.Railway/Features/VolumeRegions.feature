Feature: Persistent Railway volumes remain in the requested region
  Region configuration must precede volume creation and survive deployment materialization.

  Scenario Outline: Supported region aliases use the provider deployment identifier
    Given a Railway service with region alias <alias>
    When the regional service is deployed
    Then the explicit provider region is <provider>
    And the deployment has one replica in <airport>
    Examples:
      | alias                     | provider                  | airport |
      | ams                       | europe-west4-drams3a       | ams     |
      | europe-west4              | europe-west4-drams3a       | ams     |
      | europe-west4-drams3a       | europe-west4-drams3a       | ams     |
      | sfo                       | us-west2                  | sfo     |
      | us-west2                  | us-west2                  | sfo     |
      | iad                       | us-east4-eqdc4a           | iad     |
      | us-east4-eqdc4a            | us-east4-eqdc4a           | iad     |
      | sin                       | asia-southeast1-eqsg3a     | sin     |
      | asia-southeast1-eqsg3a     | asia-southeast1-eqsg3a     | sin     |

  Scenario: A new Amsterdam volume does not inherit the default service region
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And Railway defaults deployments without an explicit service region to SFO
    When the regional service is deployed twice
    Then region configuration precedes volume creation and deployment
    And the unchanged replay has no provider mutations
    And the persistent volume remains in AMS

  Scenario: A source build retains its Amsterdam volume on an unchanged replay
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And the regional workload uses a source build
    And Railway defaults deployments without an explicit service region to SFO
    When the regional service is deployed twice
    Then region configuration precedes volume creation and deployment
    And the unchanged replay has no provider mutations
    And the persistent volume remains in AMS

  Scenario Outline: A created volume must prove its requested binding before deployment
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And the created volume has <field> drift
    When the regional service is rejected
    Then no deployment was requested
    And the created volume remains recorded for operator reconciliation
    Examples:
      | field   |
      | region  |
      | service |
      | mount   |

  Scenario Outline: A new volume binding may become visible before its proof is complete
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And the regional volume workload uses <mode> publishing
    And the created volume initially omits its <field> proof
    When the regional service is deployed twice
    Then the volume binding completes before deployment
    And the unchanged replay has no provider mutations
    And the persistent volume remains in AMS
    Examples:
      | mode   | field   |
      | image  | service |
      | image  | region  |
      | source | service |
      | source | region  |

  Scenario: Incomplete newly created volume proof has a bounded deadline
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And the created volume never supplies its service proof
    When the created volume readback reaches its deadline
    Then no deployment was requested
    And the created volume remains recorded for operator reconciliation

  Scenario: An incomplete readback cannot conceal a wrong created service binding
    Given a Railway service with region alias ams
    And it needs a new persistent volume
    And the created volume initially omits its service proof
    And the created volume has service drift
    When the regional service is rejected
    Then no deployment was requested
    And the created volume remains recorded for operator reconciliation

  Scenario: A successful deployment cannot silently fall back to another region
    Given a Railway service with region alias ams
    And the deployment materializes in SFO
    When the regional service is rejected
    Then its sent deployment remains recorded for reconciliation

  Scenario: Existing persistent region drift requires operator migration
    Given a Railway service with region alias ams
    And an existing owned volume is in SFO
    When the regional service is rejected
    Then no provider mutation or deployment was requested

  Scenario: A provider name readback and airport alias identify the same existing region
    Given a Railway service with region alias ams
    And an existing owned volume reports its Amsterdam provider name
    When the regional service is deployed twice
    Then the unchanged replay has no provider mutations
    And the persistent volume remains in AMS

  Scenario: Package releases preserve the published integration assembly identity
    Then the Railway integration assembly version is 1.0.0.0
