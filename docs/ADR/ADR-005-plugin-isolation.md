# ADR-005: Versioned vendor plugins

**Status:** Accepted

Keep vendor behavior behind stable DTO contracts. Pin plugin ID/version on each job. Load external plugins through dedicated assembly load contexts only after host compatibility and trust checks; do not describe load contexts as sandboxes.

