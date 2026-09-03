# Export lifecycle

The legal state graph is encoded in `Domain/ExportJob.cs`; arbitrary state strings are not accepted.

```mermaid
stateDiagram-v2
  [*] --> Draft
  Draft --> Queued: explicit Start
  Queued --> Authenticating
  Authenticating --> Starting
  Starting --> WaitingForPoll: 202 + Content-Location
  Starting --> Unknown: ambiguous response
  WaitingForPoll --> Running: due/manual poll
  Running --> WaitingForPoll: 202
  Running --> ManifestReady: 200 manifest
  ManifestReady --> QueuedForDownload
  QueuedForDownload --> Downloading
  Downloading --> Validating
  Validating --> HeldForReview: serious outcome
  Validating --> Committed: clean validation
  HeldForReview --> Committed: explicit approval
  Committed --> CleanupRequested: verified local commit
  Committed --> Completed: cleanup disabled
  CleanupRequested --> Completed: DELETE then verified 404
```

Pause is a local state and never issues DELETE. Cancel Remote Export and Cleanup Remote Export both require typed confirmation, but apply at different stages and always target the original polling URL. Local deletion never implies remote deletion.

HTTP 200 means only that a manifest was returned. It does not mean files were downloaded, valid, locally committed, or remotely cleaned up.

