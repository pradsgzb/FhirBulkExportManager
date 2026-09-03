# ADR-009: Local commit before remote cleanup

**Status:** Accepted

No automatic DELETE is allowed without a freshly verified `local-commit.json`, manifest hash, listed files, sizes, and SHA-256 values. Cleanup targets only the original Content-Location and is independently verified.

