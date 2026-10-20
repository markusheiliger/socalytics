"""Attempt context mirrored from contracts/analysis/analyst-attempt/v1."""

from __future__ import annotations

from socalytics_analyst.models.common import AttemptNumber, ContractModel, ContractVersionV1, Identifier


class AttemptContext(ContractModel):
    """Identity of the claimed Execution Attempt; carries no lease, fencing token, grant, or credential."""

    contract_version: ContractVersionV1
    attempt_id: Identifier
    attempt_number: AttemptNumber
